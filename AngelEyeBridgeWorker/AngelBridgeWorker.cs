using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace AngelEyeBmsBridge;

/// <summary>
/// Headless Linux worker that connects multiple ANGEL shoes and forwards events through the BMS outbox.
/// </summary>
public sealed class AngelBridgeWorker : IAsyncDisposable
{
    private readonly WorkerSettings _settings;
    private readonly BridgeEventJournal _journal;
    private readonly WorkerStateStore _stateStore;
    private readonly BmsApiClient _bmsApiClient = new();
    private readonly List<ShoeEndpoint> _endpoints;
    private readonly ConcurrentDictionary<string, BridgeOutboxStatus> _outboxStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _publishedStartGames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _handledBmsCommandIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ShoeEndpoint, CancellationTokenSource> _pendingNextRoundCountdowns = [];
    private readonly RecoverRoundBackoffTracker _recoverRoundBackoff = new();
    private readonly WorkerHttpRouter _httpRouter;
    private readonly object _roundGate = new();
    private readonly object _commandGate = new();
    private long _eventSequence;
    private Task? _reconnectTask;
    private Task? _statusTask;
    private Task? _healthTask;
    private CancellationTokenSource? _runCts;

    public AngelBridgeWorker(WorkerSettings settings)
    {
        _settings = settings;
        _stateStore = new WorkerStateStore(settings.Bridge.StatePath);
        foreach (ShoeEndpointSettings shoe in settings.Shoes)
        {
            _stateStore.Apply(shoe);
        }

        _journal = new BridgeEventJournal(settings.Bridge.DatabasePath);
        _endpoints = settings.Shoes.Select(shoe => new ShoeEndpoint(shoe)).ToList();
        foreach (ShoeEndpoint endpoint in _endpoints)
        {
            RegisterEndpoint(endpoint);
        }

        _httpRouter = new WorkerHttpRouter(
            new WorkerQuerySource(
                settings.Bridge.InstanceName,
                settings.Bridge.EnvironmentName,
                settings.Bridge.Role),
            BuildHealthResponse,
            BuildQueryStatusSnapshot,
            _journal,
            message => Log("ERR", message));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken runToken = _runCts.Token;

        Log("SYS", $"Bridge starting. db={_journal.DbPath}");
        Log("SYS", $"State file: {_stateStore.Path}");
        StartBmsDispatcher();

        if (_settings.Health.Enabled)
        {
            _healthTask = Task.Run(() => RunHealthServerAsync(runToken), runToken);
        }

        if (_settings.Bridge.AutoConnect)
        {
            await ConnectAllAsync(runToken).ConfigureAwait(false);
        }

        _reconnectTask = Task.Run(() => ReconnectLoopAsync(runToken), runToken);
        _statusTask = Task.Run(() => StatusLoopAsync(runToken), runToken);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, runToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts = _runCts;
        if (cts != null && !cts.IsCancellationRequested)
        {
            cts.Cancel();
        }

        foreach (CancellationTokenSource pending in _pendingNextRoundCountdowns.Values)
        {
            pending.Cancel();
            pending.Dispose();
        }
        _pendingNextRoundCountdowns.Clear();

        foreach (Task? task in new[] { _reconnectTask, _statusTask, _healthTask })
        {
            if (task == null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log("WARN", $"Background task stopped with error: {ex.Message}");
            }
        }

        foreach (ShoeEndpoint endpoint in _endpoints)
        {
            try
            {
                endpoint.Disconnect();
            }
            catch (Exception ex)
            {
                Log(endpoint, "WARN", $"Disconnect failed: {ex.Message}");
            }
        }

        await _bmsApiClient.StopAsync().ConfigureAwait(false);
        Log("SYS", "Bridge stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _bmsApiClient.Dispose();
        _runCts?.Dispose();
    }

    private void RegisterEndpoint(ShoeEndpoint endpoint)
    {
        endpoint.LogReceived += (shoe, type, data) => Log(shoe, type, data);
        endpoint.CardDrawn += EndpointCardDrawn;
        endpoint.GameResultReceived += EndpointGameResultReceived;
        endpoint.ErrorOccurred += EndpointErrorOccurred;
        endpoint.LockStatusChanged += EndpointLockStatusChanged;
        endpoint.ErrorCleared += EndpointErrorCleared;
        endpoint.CuttingCardDrawn += EndpointCuttingCardDrawn;
    }

    private async Task ConnectAllAsync(CancellationToken cancellationToken)
    {
        foreach (ShoeEndpoint endpoint in _endpoints.Where(static e => e.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryConnect(endpoint);
            if (endpoint.IsConnected && _settings.Bridge.AutoStartRoundOnConnect)
            {
                await BeginInitialRoundAsync(endpoint).ConfigureAwait(false);
            }
        }
    }

    private void TryConnect(ShoeEndpoint endpoint)
    {
        if (endpoint.IsConnected || !endpoint.Enabled)
        {
            return;
        }

        try
        {
            endpoint.Connect();
            _stateStore.Save(endpoint);
            Log(endpoint, "SYS", $"Connected {endpoint.ConnectionDisplay}");
        }
        catch (Exception ex)
        {
            Log(endpoint, "WARN", $"Connect failed {endpoint.ConnectionDisplay}: {ex.Message}");
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(_settings.Bridge.ReconnectSeconds), cancellationToken).ConfigureAwait(false);
            foreach (ShoeEndpoint endpoint in _endpoints.Where(static e => e.Enabled && !e.IsConnected))
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryConnect(endpoint);
            }
        }
    }

    private async Task StatusLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await RefreshOutboxStatusesAsync().ConfigureAwait(false);
            LogHealthSummary();
            await Task.Delay(TimeSpan.FromSeconds(_settings.Bridge.StatusLogSeconds), cancellationToken).ConfigureAwait(false);
        }
    }

    private void StartBmsDispatcher()
    {
        string token = ResolveBmsToken();
        BmsApiSettings apiSettings = new(_settings.Bms.EventApiUrl, token);
        _bmsApiClient.OnLogReceived += message => Log("API", message);
        _bmsApiClient.OnStatusChanged += status => Log("API", $"BMS dispatcher: {status}");
        _bmsApiClient.Start(
            apiSettings,
            _journal,
            IsEventDispatchEnabled,
            BuildHeartbeatSnapshot,
            HandleBmsCommandAsync);
    }

    private string ResolveBmsToken()
    {
        if (!_settings.Bms.AutoGenerateJwt)
        {
            return _settings.Bms.Token;
        }

        return JwtTokenGenerator.GenerateSourceProviderToken(
            _settings.Bms.JwtNameIdentifier,
            _settings.Bms.JwtSerialNumber,
            _settings.Bms.JwtIssuer,
            _settings.Bms.JwtAudience,
            _settings.Bms.JwtSigningKey,
            _settings.Bms.JwtLifetimeMinutes);
    }

    private async Task BeginInitialRoundAsync(ShoeEndpoint endpoint)
    {
        if (!endpoint.IsConnected || endpoint.ShoeEnding)
        {
            return;
        }

        // Do not create yesterday's StartGame merely because the worker restarted
        // after midnight.  The next card tells us whether an old game is continuing
        // (for example Banker #1 after Player #1 at 23:59) or Player #1 is a new game.
        if (endpoint.CurrentShoe > 0 && !BridgeGameNumbering.IsShoeForDate(endpoint.CurrentShoe, DateTime.Now))
        {
            Log(endpoint, "SYS", $"跨日啟動，保留 {endpoint.CurrentShoe}/{endpoint.CurrentRound} 並等待下一筆牌訊判定是否為新局。");
            return;
        }

        await BeginRoundCountdownAsync(endpoint, DateTimeOffset.UtcNow, endpoint.CurrentRound > 0 ? "啟動時延續當前局" : "啟動時建立第一局").ConfigureAwait(false);
    }

    private async Task BeginRoundCountdownAsync(ShoeEndpoint endpoint, DateTimeOffset startedAtUtc, string reason)
    {
        lock (_roundGate)
        {
            long targetRound = endpoint.CurrentRound > 0 ? endpoint.CurrentRound : 1;
            endpoint.SetGameNumber(endpoint.CurrentShoe, Math.Max(targetRound - 1, 0));
            endpoint.BeginNextRoundCountdown();
            endpoint.StartBetCountdown(startedAtUtc, endpoint.TotalBetTimeSeconds);
        }

        Log(endpoint, "SYS", $"{reason}: {endpoint.CurrentShoe}/{endpoint.CurrentRound}");
        _stateStore.Save(endpoint);
        await PublishStartGameIfNeededAsync(endpoint, startedAtUtc).ConfigureAwait(false);
    }

    private async void EndpointCardDrawn(ShoeEndpoint endpoint, SerialListener.CardInfo card)
    {
        try
        {
            if (card.EventCode != 'R' && card.Target == "Player" && card.Index == 1)
            {
                CancelPendingNextRound(endpoint);
                await PublishStartGameIfNeededAsync(endpoint).ConfigureAwait(false);
            }

            _stateStore.Save(endpoint);
            Log(endpoint, "EVENT", $"CardDrawn {endpoint.CurrentShoe}/{endpoint.CurrentRound} {card.Target} #{card.Index} {card.Suit} {card.Value}");
            if (!IsBaccaratCardForBms(card))
            {
                Log(endpoint, "SYS", $"牌盒狀態更新，不送 BMS CardDrawn: {card.EventCode}/{card.Target} #{card.Index}");
                return;
            }

            await PublishBridgeEventAsync("CardDrawn", endpoint, new
            {
                target = card.Target,
                index = card.Index,
                suit = card.Suit,
                value = card.Value
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log(endpoint, "ERR", $"CardDrawn handling failed: {ex.Message}");
        }
    }

    private async void EndpointGameResultReceived(ShoeEndpoint endpoint, SerialListener.GameResult result)
    {
        try
        {
            Log(endpoint, "EVENT", $"GameResult {endpoint.CurrentShoe}/{endpoint.CurrentRound} {result.Result} / {result.Pair}");
            _stateStore.Save(endpoint);
            await PublishBridgeEventAsync("GameResult", endpoint, new
            {
                result = result.Result,
                pair = result.Pair
            }).ConfigureAwait(false);

            if (_settings.Bridge.AutoStartNextRoundAfterResult && !endpoint.ShoeEnding)
            {
                ScheduleNextRoundCountdownAfterResult(endpoint);
            }
        }
        catch (Exception ex)
        {
            Log(endpoint, "ERR", $"GameResult handling failed: {ex.Message}");
        }
    }

    private async void EndpointCuttingCardDrawn(ShoeEndpoint endpoint, SerialListener.CutCardInfo cutCard)
    {
        try
        {
            CancelPendingNextRound(endpoint);
            Log(endpoint, "EVENT", $"CutCardDrawn {endpoint.CurrentShoe}/{endpoint.CurrentRound}");
            _stateStore.Save(endpoint);
            await PublishBridgeEventAsync("CutCardDrawn", endpoint, new
            {
                shoeEnding = true,
                rawBytes = cutCard.RawBytes
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log(endpoint, "ERR", $"CutCard handling failed: {ex.Message}");
        }
    }

    private async void EndpointErrorOccurred(ShoeEndpoint endpoint, SerialListener.ErrorInfo error)
    {
        try
        {
            CancelPendingNextRound(endpoint);
            Log(endpoint, "EVENT", $"Error [{error.ErrorCode}] {error.ErrorMessage}");
            _stateStore.Save(endpoint);
            await PublishBridgeEventAsync("Error", endpoint, new
            {
                errorCode = error.ErrorCode,
                errorMessage = error.ErrorMessage,
                inErrorMode = error.InErrorMode
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log(endpoint, "ERR", $"Error handling failed: {ex.Message}");
        }
    }

    private async void EndpointLockStatusChanged(ShoeEndpoint endpoint, bool isLocked)
    {
        try
        {
            Log(endpoint, "EVENT", isLocked ? "LockStatus Locked" : "LockStatus Unlocked");
            await PublishBridgeEventAsync("LockStatus", endpoint, new { isLocked }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log(endpoint, "ERR", $"LockStatus handling failed: {ex.Message}");
        }
    }

    private async void EndpointErrorCleared(ShoeEndpoint endpoint, int errorCode, string errorMessage)
    {
        try
        {
            Log(endpoint, "EVENT", $"ErrorCleared [{errorCode}] {errorMessage}");
            await PublishBridgeEventAsync("ErrorCleared", endpoint, new
            {
                errorCode,
                errorMessage
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log(endpoint, "ERR", $"ErrorCleared handling failed: {ex.Message}");
        }
    }

    private void ScheduleNextRoundCountdownAfterResult(ShoeEndpoint endpoint)
    {
        CancelPendingNextRound(endpoint);
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_runCts?.Token ?? CancellationToken.None);
        _pendingNextRoundCountdowns[endpoint] = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                int delaySeconds = _settings.Bridge.ResultToNextRoundDelaySeconds;
                Log(endpoint, "SYS", $"結算後 {delaySeconds} 秒自動進入下一局倒數。");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cts.Token).ConfigureAwait(false);
                if (!endpoint.IsConnected || endpoint.InErrorMode || endpoint.ShoeEnding)
                {
                    return;
                }

                lock (_roundGate)
                {
                    endpoint.BeginNextRoundCountdown();
                    endpoint.StartBetCountdown(DateTimeOffset.UtcNow, endpoint.TotalBetTimeSeconds);
                }

                _stateStore.Save(endpoint);
                await PublishStartGameIfNeededAsync(endpoint).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log(endpoint, "ERR", $"Next round schedule failed: {ex.Message}");
            }
            finally
            {
                if (_pendingNextRoundCountdowns.Remove(endpoint))
                {
                    cts.Dispose();
                }
            }
        });
    }

    private void CancelPendingNextRound(ShoeEndpoint endpoint)
    {
        if (_pendingNextRoundCountdowns.Remove(endpoint, out CancellationTokenSource? pending))
        {
            pending.Cancel();
            pending.Dispose();
        }
    }

    private async Task<bool> PublishStartGameIfNeededAsync(ShoeEndpoint endpoint, DateTimeOffset? startTimeOverride = null)
    {
        string key = $"{endpoint.SourceDataCode}:{endpoint.CurrentShoe}:{endpoint.CurrentRound}";
        lock (_roundGate)
        {
            if (_publishedStartGames.Contains(key))
            {
                return true;
            }
        }

        int totalBetTime = endpoint.TotalBetTimeSeconds;
        DateTimeOffset startTime = startTimeOverride ?? DateTimeOffset.UtcNow;
        bool queued = await PublishBridgeEventAsync("StartGame", endpoint, new
        {
            totalBetTime,
            startTime = startTime.ToString("o", CultureInfo.InvariantCulture),
            bootId = endpoint.CurrentShoe.ToString(CultureInfo.InvariantCulture),
            groupId = 1
        }, rootFields =>
        {
            rootFields["totalBetTime"] = totalBetTime;
            rootFields["startTime"] = startTime.ToString("o", CultureInfo.InvariantCulture);
        }).ConfigureAwait(false);

        if (queued)
        {
            lock (_roundGate)
            {
                _publishedStartGames.Add(key);
            }

            Log(endpoint, "EVENT", $"StartGame {endpoint.CurrentShoe}/{endpoint.CurrentRound} TotalBetTime={totalBetTime}");
        }

        return queued;
    }

    private async Task<bool> PublishBridgeEventAsync(string type, ShoeEndpoint endpoint, object data, Action<Dictionary<string, object?>>? configureRoot = null)
    {
        if (!endpoint.BmsTransmitEnabled)
        {
            Log(endpoint, "API", $"BMS 傳送已關閉，略過 {type} {endpoint.CurrentShoe}/{endpoint.CurrentRound}");
            return false;
        }

        Dictionary<string, object?> payload = new()
        {
            ["type"] = type,
            ["source"] = AngelEyeProtocol.SourceName,
            ["sequence"] = Interlocked.Increment(ref _eventSequence),
            ["timestamp"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["deskName"] = endpoint.DeskName,
            ["sourceDataCode"] = endpoint.SourceDataCode,
            ["shoeId"] = endpoint.ShoeId,
            ["deviceId"] = endpoint.DeviceId,
            ["shoe"] = endpoint.CurrentShoe,
            ["round"] = endpoint.CurrentRound,
            ["roundId"] = endpoint.CurrentRoundId,
            ["shoeRound"] = endpoint.ShoeRound,
            ["state"] = ResolveEventState(type),
            ["data"] = data,
            ["connectionMode"] = endpoint.ConnectionMode
        };
        configureRoot?.Invoke(payload);

        if (Guid.TryParse(endpoint.SourceDataId, out Guid sourceDataId) && sourceDataId != Guid.Empty)
        {
            payload["sourceDataId"] = endpoint.SourceDataId;
        }

        if (!string.IsNullOrWhiteSpace(endpoint.ComPort))
        {
            payload["comPort"] = endpoint.ComPort;
        }

        if (endpoint.IsMoxaTcpMode)
        {
            payload["moxaHost"] = endpoint.MoxaHost;
            payload["moxaPort"] = endpoint.MoxaPort;
        }

        long eventId = await _journal.AppendAsync(payload).ConfigureAwait(false);
        Log(endpoint, "API", $"Outbox queued #{eventId} {type} {endpoint.CurrentShoe}/{endpoint.CurrentRound}");
        _ = RefreshOutboxStatusesAsync();
        return true;
    }

    private static string ResolveEventState(string type) => type switch
    {
        "StartGame" => "Countdown",
        "CardDrawn" => "Dealing",
        "GameResult" => "Settled",
        "CutCardDrawn" => "ShoeEnding",
        "Error" => "Error",
        "ErrorCleared" => "Normal",
        "LockStatus" => "Locked",
        _ => "Event"
    };

    private static bool IsBaccaratCardForBms(SerialListener.CardInfo card)
    {
        return card.Target == "Player" || card.Target == "Banker";
    }

    private IReadOnlyList<AngelBridgeHeartbeatEndpointStatus> BuildHeartbeatSnapshot()
    {
        return _endpoints.Select(endpoint =>
        {
            BridgeOutboxStatus outboxStatus = GetEndpointOutboxStatus(endpoint);
            return new AngelBridgeHeartbeatEndpointStatus
            {
                DeskName = endpoint.DeskName,
                SourceDataCode = endpoint.SourceDataCode,
                SourceDataId = endpoint.SourceDataId,
                ShoeId = endpoint.ShoeId,
                DeviceId = endpoint.DeviceId,
                ComPort = endpoint.ConnectionDisplay,
                ConnectionMode = endpoint.ConnectionMode,
                MoxaHost = endpoint.IsMoxaTcpMode ? endpoint.MoxaHost : string.Empty,
                MoxaPort = endpoint.IsMoxaTcpMode ? endpoint.MoxaPort : null,
                Enabled = endpoint.Enabled,
                BmsTransmitEnabled = endpoint.BmsTransmitEnabled,
                MockMode = endpoint.MockMode,
                Status = endpoint.StatusText,
                Shoe = endpoint.CurrentShoe,
                Round = endpoint.CurrentRound,
                RoundId = endpoint.CurrentRoundId,
                PendingOutboxCount = outboxStatus.PendingCount,
                FailedOutboxCount = outboxStatus.FailedCount,
                LastEvent = endpoint.LastEventText
            };
        }).ToList();
    }

    private async Task RefreshOutboxStatusesAsync()
    {
        foreach (ShoeEndpoint endpoint in _endpoints)
        {
            try
            {
                BridgeOutboxStatus status = await _journal.GetOutboxStatusAsync(endpoint.SourceDataCode, endpoint.DeviceId).ConfigureAwait(false);
                _outboxStatuses[GetEndpointKey(endpoint)] = status;
            }
            catch (Exception ex)
            {
                Log(endpoint, "WARN", $"Outbox status failed: {ex.Message}");
            }
        }
    }

    private BridgeOutboxStatus GetEndpointOutboxStatus(ShoeEndpoint endpoint)
    {
        return _outboxStatuses.TryGetValue(GetEndpointKey(endpoint), out BridgeOutboxStatus? status)
            ? status
            : BridgeOutboxStatus.Empty;
    }

    private static string GetEndpointKey(ShoeEndpoint endpoint) => $"{endpoint.SourceDataCode}:{endpoint.DeviceId}";

    private async Task<BridgeCommandHandlingResult> HandleBmsCommandAsync(AngelBridgeCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        bool alreadyHandled;
        lock (_commandGate)
        {
            alreadyHandled = !string.IsNullOrWhiteSpace(command.CommandId) && _handledBmsCommandIds.Contains(command.CommandId);
        }

        BridgeCommandHandlingResult result;
        if (alreadyHandled)
        {
            result = BridgeCommandHandlingResult.Handled("Command was already handled in this bridge session.");
        }
        else
        {
            string type = command.Type.Trim();
            result = type switch
            {
                "RecoverRound" => await HandleRecoverRoundCommandAsync(command, cancellationToken).ConfigureAwait(false),
                "ResendEvent" => await HandleResendEventCommandAsync(command, cancellationToken).ConfigureAwait(false),
                _ => BridgeCommandHandlingResult.Rejected($"Unsupported command type: {command.Type}")
            };
        }

        await RecordCommandAuditAsync(command, result, observedAt).ConfigureAwait(false);

        if (!alreadyHandled && result.Success && !string.IsNullOrWhiteSpace(command.CommandId))
        {
            lock (_commandGate)
            {
                _handledBmsCommandIds.Add(command.CommandId);
            }
        }

        return result;
    }

    private async Task RecordCommandAuditAsync(
        AngelBridgeCommand command,
        BridgeCommandHandlingResult result,
        DateTimeOffset observedAt)
    {
        string auditResult = result.Status switch
        {
            "Deferred" => "Backoff",
            "Handled" when result.Message.StartsWith("Requeued", StringComparison.OrdinalIgnoreCase) => "Requeued",
            _ => result.Status
        };
        DateTimeOffset? nextRetryUtc = null;
        if (string.Equals(command.Type, "RecoverRound", StringComparison.Ordinal) &&
            auditResult is "Backoff" or "NotFound")
        {
            RecoverRoundBackoffDecision decision = _recoverRoundBackoff.GetDecision(command, observedAt);
            if (!decision.ShouldAttempt)
            {
                nextRetryUtc = observedAt.Add(decision.Delay);
            }
        }

        string commandId = string.IsNullOrWhiteSpace(command.CommandId)
            ? $"local-{Guid.NewGuid():N}"
            : command.CommandId;
        await _journal.RecordRecoveryRequestAsync(new BridgeRecoveryAudit(
            commandId,
            command.Type.Trim(),
            command.SourceDataCode,
            command.DeviceId,
            command.Shoe,
            command.Round,
            command.RoundId,
            observedAt,
            DateTimeOffset.UtcNow,
            auditResult,
            nextRetryUtc,
            result.Message)).ConfigureAwait(false);
    }

    private async Task<BridgeCommandHandlingResult> HandleRecoverRoundCommandAsync(AngelBridgeCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!command.Shoe.HasValue || !command.Round.HasValue)
        {
            return BridgeCommandHandlingResult.Rejected("RecoverRound requires shoe and round.");
        }

        ShoeEndpoint? endpoint = FindEndpointForCommand(command);
        if (endpoint == null)
        {
            return BridgeCommandHandlingResult.NotFound("Target endpoint was not found.");
        }

        if (!endpoint.BmsTransmitEnabled)
        {
            return BridgeCommandHandlingResult.Rejected("Target endpoint has BMS transmission disabled.");
        }

        RecoverRoundBackoffDecision backoffDecision = _recoverRoundBackoff.GetDecision(command, DateTimeOffset.UtcNow);
        if (!backoffDecision.ShouldAttempt)
        {
            return BridgeCommandHandlingResult.Deferred(
                $"GameResult {command.Shoe}/{command.Round} was not found locally; retry after {FormatRetryDelay(backoffDecision.Delay)}.");
        }

        BridgeEventQuery query = new()
        {
            Type = "GameResult",
            SourceDataCode = endpoint.SourceDataCode,
            DeviceId = endpoint.DeviceId,
            Shoe = command.Shoe,
            Round = command.Round,
            RoundId = command.RoundId,
            Limit = 1
        };

        int count = await _journal
            .RequeueMatchingEventsAsync(query, DateTime.UtcNow, $"BMS command {command.CommandId} RecoverRound")
            .ConfigureAwait(false);
        if (count <= 0)
        {
            RecoverRoundBackoffDecision retryDecision = _recoverRoundBackoff.RecordNotFound(command, DateTimeOffset.UtcNow);
            Log(endpoint, "API", $"BMS 補償要求找不到 GameResult {command.Shoe}/{command.Round}，{FormatRetryDelay(retryDecision.Delay)} 後重試。");
            return BridgeCommandHandlingResult.NotFound($"GameResult {command.Shoe}/{command.Round} was not found locally.");
        }

        _recoverRoundBackoff.Clear(command);
        Log(endpoint, "API", $"BMS 補償要求已重新排送 GameResult {command.Shoe}/{command.Round}。");
        _ = RefreshOutboxStatusesAsync();
        return BridgeCommandHandlingResult.Handled($"Requeued {count} GameResult event(s).");
    }

    private static string FormatRetryDelay(TimeSpan delay)
    {
        int seconds = Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds));
        return $"{seconds.ToString(CultureInfo.InvariantCulture)} 秒";
    }

    private async Task<BridgeCommandHandlingResult> HandleResendEventCommandAsync(AngelBridgeCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (command.EventId.HasValue)
        {
            int requeuedById = await _journal
                .RequeueEventAsync(command.EventId.Value, DateTime.UtcNow, $"BMS command {command.CommandId} ResendEvent")
                .ConfigureAwait(false);
            if (requeuedById <= 0)
            {
                return BridgeCommandHandlingResult.NotFound($"EventId {command.EventId.Value} was not found locally.");
            }

            Log(FindEndpointForCommand(command), "API", $"BMS 要求重送事件 #{command.EventId.Value}，已重新排送。");
            _ = RefreshOutboxStatusesAsync();
            return BridgeCommandHandlingResult.Handled($"Requeued event #{command.EventId.Value}.");
        }

        ShoeEndpoint? endpoint = FindEndpointForCommand(command);
        if (endpoint == null)
        {
            return BridgeCommandHandlingResult.NotFound("Target endpoint was not found.");
        }

        if (!endpoint.BmsTransmitEnabled)
        {
            return BridgeCommandHandlingResult.Rejected("Target endpoint has BMS transmission disabled.");
        }

        if (string.IsNullOrWhiteSpace(command.EventType) && (!command.Shoe.HasValue || !command.Round.HasValue))
        {
            return BridgeCommandHandlingResult.Rejected("ResendEvent requires eventId or eventType with shoe and round.");
        }

        BridgeEventQuery query = new()
        {
            Type = command.EventType,
            SourceDataCode = endpoint.SourceDataCode,
            DeviceId = endpoint.DeviceId,
            Shoe = command.Shoe,
            Round = command.Round,
            RoundId = command.RoundId,
            Limit = 20
        };

        int requeuedCount = await _journal
            .RequeueMatchingEventsAsync(query, DateTime.UtcNow, $"BMS command {command.CommandId} ResendEvent")
            .ConfigureAwait(false);
        if (requeuedCount <= 0)
        {
            return BridgeCommandHandlingResult.NotFound("No matching local events were found.");
        }

        Log(endpoint, "API", $"BMS 要求重送 {requeuedCount} 筆事件，已重新排送。");
        _ = RefreshOutboxStatusesAsync();
        return BridgeCommandHandlingResult.Handled($"Requeued {requeuedCount} event(s).");
    }

    private ShoeEndpoint? FindEndpointForCommand(AngelBridgeCommand command)
    {
        return _endpoints.FirstOrDefault(endpoint =>
            (string.IsNullOrWhiteSpace(command.SourceDataCode) ||
                string.Equals(endpoint.SourceDataCode, command.SourceDataCode, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(command.DeviceId) ||
                string.Equals(endpoint.DeviceId, command.DeviceId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(endpoint.ShoeId, command.DeviceId, StringComparison.OrdinalIgnoreCase)));
    }

    private bool IsEventDispatchEnabled(BridgePendingEvent pending)
    {
        ShoeEndpoint? endpoint = _endpoints.FirstOrDefault(candidate =>
            string.Equals(candidate.SourceDataCode, pending.SourceDataCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.DeviceId, pending.DeviceId, StringComparison.OrdinalIgnoreCase));

        return endpoint?.BmsTransmitEnabled ?? true;
    }

    private async Task RunHealthServerAsync(CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(_settings.Health.Host, out IPAddress? address))
        {
            address = IPAddress.Loopback;
        }

        TcpListener listener = new(address, _settings.Health.Port);
        listener.Start();
        Log("SYS", $"Health check listening on http://{_settings.Health.Host}:{_settings.Health.Port}/health");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                _ = Task.Run(() => HandleHealthClientAsync(client, cancellationToken), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleHealthClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        await using NetworkStream stream = client.GetStream();
        using StreamReader reader = new(stream, Encoding.ASCII, leaveOpen: true);
        string? requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        string[] requestParts = requestLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        string method = requestParts.ElementAtOrDefault(0) ?? "GET";
        string target = requestParts.ElementAtOrDefault(1) ?? "/health";

        while (!string.IsNullOrWhiteSpace(await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
        {
        }

        WorkerHttpResponse response;
        try
        {
            response = await _httpRouter.RouteAsync(method, target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("ERR", $"Query request failed: {ex.Message}");
            response = new WorkerHttpResponse(
                500,
                "Internal Server Error",
                "{\"error\":{\"code\":\"internal_error\",\"message\":\"The query could not be completed.\"}}");
        }

        byte[] bodyBytes = Encoding.UTF8.GetBytes(response.Body);
        string header = $"HTTP/1.1 {response.Status} {response.ReasonPhrase}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        byte[] headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        client.Dispose();
    }

    private (int Status, string Body) BuildHealthResponse(string path)
    {
        bool detailed = path.StartsWith("/health", StringComparison.OrdinalIgnoreCase);
        List<object> endpoints = [];
        bool allEnabledConnected = true;
        foreach (ShoeEndpoint endpoint in _endpoints)
        {
            BridgeOutboxStatus outbox = GetEndpointOutboxStatus(endpoint);
            if (endpoint.Enabled && !endpoint.IsConnected)
            {
                allEnabledConnected = false;
            }

            if (detailed)
            {
                endpoints.Add(new
                {
                    endpoint.DeskName,
                    endpoint.SourceDataCode,
                    endpoint.ShoeId,
                    endpoint.ConnectionMode,
                    endpoint.MoxaHost,
                    endpoint.MoxaPort,
                    endpoint.Enabled,
                    endpoint.IsConnected,
                    endpoint.StatusText,
                    endpoint.CurrentShoe,
                    endpoint.CurrentRound,
                    endpoint.LastEventText,
                    outbox.PendingCount,
                    outbox.FailedCount
                });
            }
        }

        var payload = new
        {
            ok = allEnabledConnected,
            bridgeId = _settings.Bridge.BridgeId,
            bridgeName = _settings.Bridge.BridgeName,
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            utc = DateTimeOffset.UtcNow,
            bmsDispatcher = _bmsApiClient.IsRunning,
            endpoints = detailed ? endpoints : null
        };

        return (allEnabledConnected ? 200 : 503, JsonSerializer.Serialize(payload, WorkerSettings.JsonOptions));
    }

    private WorkerStatusData BuildQueryStatusSnapshot()
    {
        List<WorkerEndpointStatusData> endpoints = _endpoints.Select(endpoint =>
        {
            BridgeOutboxStatus outbox = GetEndpointOutboxStatus(endpoint);
            DateTimeOffset? lastEventUtc = endpoint.LastEventAt.HasValue
                ? new DateTimeOffset(endpoint.LastEventAt.Value).ToUniversalTime()
                : null;
            return new WorkerEndpointStatusData(
                endpoint.DeskName,
                endpoint.SourceDataCode,
                endpoint.DeviceId,
                endpoint.Enabled,
                endpoint.IsConnected,
                endpoint.StatusText,
                lastEventUtc,
                endpoint.LastEventText,
                endpoint.CurrentShoe,
                endpoint.CurrentRound,
                endpoint.CurrentRoundId,
                endpoint.BmsTransmitEnabled,
                outbox.PendingCount,
                outbox.FailedCount);
        }).ToList();

        return new WorkerStatusData(
            _settings.Bridge.BridgeId,
            _settings.Bridge.BridgeName,
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            _bmsApiClient.IsRunning,
            endpoints);
    }

    private void LogHealthSummary()
    {
        string summary = string.Join(" | ", _endpoints.Select(endpoint =>
        {
            BridgeOutboxStatus outbox = GetEndpointOutboxStatus(endpoint);
            string connected = endpoint.IsConnected ? "up" : "down";
            return $"{endpoint.SourceDataCode} {connected} {endpoint.ConnectionDisplay} shoe={endpoint.CurrentShoe} round={endpoint.CurrentRound} pending={outbox.PendingCount} failed={outbox.FailedCount} last={endpoint.LastEventText}";
        }));
        Log("HEALTH", summary);
    }

    private void Log(ShoeEndpoint? endpoint, string type, string message)
    {
        string prefix = endpoint == null
            ? "[ANGEL]"
            : $"[ANGEL][{endpoint.SourceDataCode}/{endpoint.ShoeId}]";
        Console.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {prefix}[{type}] {message}");
    }

    private void Log(string type, string message)
    {
        Log(null, type, message);
    }
}
