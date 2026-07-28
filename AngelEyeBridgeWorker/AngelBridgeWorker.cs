using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
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
    private BmsClientCredentialsAccessTokenProvider? _bmsAccessTokenProvider;
    private readonly List<ShoeEndpoint> _endpoints;
    private readonly ConcurrentDictionary<string, BridgeOutboxStatus> _outboxStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _createdStartGames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _creatingStartGames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _publishedStartGames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _handledBmsCommandIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ShoeEndpoint, CancellationTokenSource> _pendingNextRoundCountdowns = [];
    private readonly WorkerHttpRouter _httpRouter;
    private readonly object _roundGate = new();
    private readonly object _commandGate = new();
    private readonly SemaphoreSlim _rawFrameGate = new(1, 1);
    private long _eventSequence;
    private Task? _reconnectTask;
    private Task? _statusTask;
    private Task? _healthTask;
    private CancellationTokenSource? _runCts;

    internal bool IsBmsDispatcherRunning => _bmsApiClient.IsRunning;

    internal IReadOnlyList<ShoeEndpoint> Endpoints => _endpoints;

    internal BridgeEventJournal Journal => _journal;

    public AngelBridgeWorker(WorkerSettings settings)
    {
        _settings = settings;
        _stateStore = new WorkerStateStore(settings.Bridge.StatePath);
        foreach (ShoeEndpointSettings shoe in settings.Shoes)
        {
            _stateStore.Apply(shoe);
        }

        _journal = new BridgeEventJournal(settings.Bridge.DatabasePath);
        _endpoints = settings.Shoes
            .Select(shoe => new ShoeEndpoint(shoe)
            {
                AutoAdvanceRoundFromEvents = false
            })
            .ToList();
        foreach (ShoeEndpoint endpoint in _endpoints)
        {
            _stateStore.Apply(endpoint);
            RestoreStartGameTracking(endpoint);
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
        if (HasAuthorizedBmsSender())
        {
            StartBmsDispatcher();
        }
        else
        {
            Log("SYS", "All BMS transmission is disabled; dispatcher not started.");
        }

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

        CancellationTokenSource[] pendingCountdowns;
        lock (_roundGate)
        {
            pendingCountdowns = _pendingNextRoundCountdowns.Values.ToArray();
            _pendingNextRoundCountdowns.Clear();
        }
        foreach (CancellationTokenSource pending in pendingCountdowns)
        {
            pending.Cancel();
        }

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
                LogException(null, "WARN", "Background task stopped with error", ex);
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
                LogException(endpoint, "WARN", "Disconnect failed", ex);
            }
        }

        await _bmsApiClient.StopAsync().ConfigureAwait(false);
        Log("SYS", "Bridge stopped.");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _bmsApiClient.Dispose();
        _bmsAccessTokenProvider?.Dispose();
        _bmsAccessTokenProvider = null;
        _runCts?.Dispose();
    }

    private void RegisterEndpoint(ShoeEndpoint endpoint)
    {
        endpoint.Listener.RawFrameAdmission =
            bytes => PersistRawFrame(endpoint, bytes.Span);
        endpoint.LogReceived += (shoe, type, data) =>
        {
            Log(shoe, type, data);
        };
        endpoint.ProtocolSignalObserved += (shoe, signal) =>
            RunSerializedEndpointEvent(
                shoe,
                () => EndpointProtocolSignalObservedAsync(shoe, signal));
        endpoint.TransportStateChanged += (shoe, state) =>
            RunSerializedEndpointEvent(
                shoe,
                () => EndpointTransportStateChangedAsync(shoe, state));
        endpoint.CardDrawn += (shoe, card) =>
            RunSerializedEndpointEvent(shoe, () => EndpointCardDrawnAsync(shoe, card));
        endpoint.GameResultReceived += (shoe, result) =>
            RunSerializedEndpointEvent(shoe, () => EndpointGameResultReceivedAsync(shoe, result));
        endpoint.ErrorOccurred += (shoe, error) =>
            RunSerializedEndpointEvent(shoe, () => EndpointErrorOccurredAsync(shoe, error));
        endpoint.LockStatusChanged += (shoe, isLocked) =>
            RunSerializedEndpointEvent(shoe, () => EndpointLockStatusChangedAsync(shoe, isLocked));
        endpoint.ErrorCleared += (shoe, code, message) =>
            RunSerializedEndpointEvent(shoe, () => EndpointErrorClearedAsync(shoe, code, message));
        endpoint.CuttingCardDrawn += (shoe, cutCard) =>
            RunSerializedEndpointEvent(shoe, () => EndpointCuttingCardDrawnAsync(shoe, cutCard));
    }

    private void RunSerializedEndpointEvent(
        ShoeEndpoint endpoint,
        Func<Task> handler)
    {
        endpoint.ExecuteSerializedStateTransition(() =>
        {
            try
            {
                handler().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                CancelPendingNextRound(endpoint);
                endpoint.MarkAlignmentRequired(
                    $"Serialized endpoint event failed: {ex.GetType().Name}.");
                TrySaveFailClosedState(endpoint);
                LogException(endpoint, "ERR", "Serialized endpoint event failed", ex);
            }
        });
    }

    private Task EndpointTransportStateChangedAsync(
        ShoeEndpoint endpoint,
        SerialListener.TransportState state)
    {
        if (state.Kind is not (
                SerialListener.TransportStateKind.RemoteClosed or
                SerialListener.TransportStateKind.ReadError))
        {
            return Task.CompletedTask;
        }

        CancelPendingNextRound(endpoint);
        endpoint.MarkAlignmentRequired(
            $"Transport {state.Kind}; round continuity cannot be proven.");
        _stateStore.Save(endpoint);
        return Task.CompletedTask;
    }

    private async Task EndpointProtocolSignalObservedAsync(
        ShoeEndpoint endpoint,
        SerialListener.ProtocolSignal signal)
    {
        Log(
            endpoint,
            "PROTOCOL",
            $"{signal.Kind} seq={signal.Sequence}; shoeEnding={endpoint.ShoeEnding}, phase={endpoint.RoundPhase}.");
        if (signal.Kind != SerialListener.ProtocolSignalKind.StartOfCommunication ||
            !endpoint.ShoeEnding)
        {
            Log(
                endpoint,
                "PROTOCOL",
                $"{signal.Kind} seq={signal.Sequence}; diagnostic only, no same-desk cut-card hold.");
            return;
        }

        CancelPendingNextRound(endpoint);
        long previousShoe = endpoint.CurrentShoe;
        long previousRound = endpoint.CurrentRound;
        string previousPhase = endpoint.RoundPhase;
        bool incompleteOldRound =
            endpoint.StartGameEventUid.HasValue &&
            previousPhase is BridgeRoundPhases.Countdown or BridgeRoundPhases.Dealing;
        if (incompleteOldRound)
        {
            await PublishBridgeEventAsync(
                "IncompleteAtShoeChange",
                endpoint,
                new
                {
                    previousShoe,
                    previousRound,
                    previousPhase,
                    trigger = "CuttingCardDrawn->StartOfCommunication",
                    protocolSequence = signal.Sequence,
                    rawBytes = signal.RawBytes
                }).ConfigureAwait(false);
            Log(
                endpoint,
                "WARN",
                $"同桌 C 後於舊局未完成時收到 S；保留 {previousShoe}/{previousRound} 為 IncompleteAtShoeChange，不補造賽果。");
        }

        if (!endpoint.TryConfirmNewShoeFromStartSignal(signal))
        {
            return;
        }

        _stateStore.Save(endpoint);
        await PublishBridgeEventAsync(
            "NewShoeConfirmed",
            endpoint,
            new
            {
                previousShoe,
                previousRound,
                newShoe = endpoint.CurrentShoe,
                newRound = endpoint.CurrentRound,
                trigger = "CuttingCardDrawn->StartOfCommunication",
                protocolSequence = signal.Sequence,
                rawBytes = signal.RawBytes,
                incompleteOldRound
            }).ConfigureAwait(false);
        Log(
            endpoint,
            "SYS",
            $"牌盒自動換靴完成: {previousShoe}/{previousRound} -> {endpoint.CurrentShoe}/0；S seq={signal.Sequence}；等待可信開局邊界。");
    }

    private Task ConnectAllAsync(CancellationToken cancellationToken)
    {
        foreach (ShoeEndpoint endpoint in _endpoints.Where(static e => e.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryConnect(endpoint);
        }

        return Task.CompletedTask;
    }

    private void TryConnect(ShoeEndpoint endpoint)
    {
        if (endpoint.IsConnected || !endpoint.Enabled)
        {
            return;
        }

        try
        {
            endpoint.ExecuteSerializedStateTransition(() =>
            {
                endpoint.Connect();
                _stateStore.Save(endpoint);
                Log(endpoint, "SYS", $"Connected {endpoint.ConnectionDisplay}");
            });
        }
        catch (Exception ex)
        {
            LogException(
                endpoint,
                "WARN",
                $"Connect failed {endpoint.ConnectionDisplay}",
                ex);
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
        BmsApiSettings apiSettings = new(
            _settings.Bms.EventApiUrl,
            string.Empty,
            _settings.Bridge.BridgeId,
            _settings.Bridge.BridgeName,
            _settings.Bridge.EnvironmentName);
        BmsClientCredentialsAccessTokenProvider accessTokenProvider = new(
            _settings.Bms.EventApiUrl,
            _settings.Bms.ClientId,
            _settings.Bms.ClientSecret,
            _settings.Bridge.BridgeId);
        _bmsAccessTokenProvider = accessTokenProvider;
        _bmsApiClient.OnLogReceived += message => Log("API", message);
        _bmsApiClient.OnStatusChanged += status => Log("API", $"BMS dispatcher: {status}");
        try
        {
            _bmsApiClient.Start(
                apiSettings,
                _journal,
                IsEventDispatchEnabled,
                BuildHeartbeatSnapshot,
                HandleBmsCommandAsync,
                accessTokenProvider: accessTokenProvider);
        }
        catch
        {
            _bmsAccessTokenProvider = null;
            accessTokenProvider.Dispose();
            throw;
        }
    }

    private async Task EndpointCardDrawnAsync(ShoeEndpoint endpoint, SerialListener.CardInfo card)
    {
        try
        {
            if (card.EventCode != 'R' && IsBaccaratCardForBms(card))
            {
                CancelPendingNextRound(endpoint);
                if (!HasCreatedStartGame(endpoint))
                {
                    endpoint.MarkAlignmentRequired(
                        $"Physical card arrived before a durable StartGame for {endpoint.CurrentShoe}/{endpoint.CurrentRound}.");
                    Log(endpoint, "SYS", $"牌訊早於合法 StartGame；本局 {endpoint.CurrentShoe}/{endpoint.CurrentRound} 僅保留本機，不補造開局。");
                }
                else
                {
                    endpoint.MarkDealing();
                }
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
                eventCode = card.EventCode.ToString(),
                accepted = IsAuthoritativeBaccaratCard(endpoint, card),
                target = card.Target,
                index = card.Index,
                suit = card.Suit,
                value = card.Value,
                protocolSequence = card.Seq,
                rawBytes = card.RawBytes
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            endpoint.MarkAlignmentRequired($"Card persistence failed: {ex.GetType().Name}.");
            TrySaveFailClosedState(endpoint);
            LogException(endpoint, "ERR", "CardDrawn handling failed", ex);
        }
    }

    private async Task EndpointGameResultReceivedAsync(ShoeEndpoint endpoint, SerialListener.GameResult result)
    {
        try
        {
            if (endpoint.AwaitingFirstAuthoritativeResultAfterShoeChange)
            {
                Log(
                    endpoint,
                    "WARN",
                    $"新靴尚未具備合法 StartGame 與完整必要牌面，隔離遲到 GameResult {result.Result}/{result.Pair}，不改變新靴狀態。");
                await PublishBridgeEventAsync(
                    "LateGameResultAfterShoeChange",
                    endpoint,
                    new
                    {
                        result = result.Result,
                        pair = result.Pair,
                        protocolSequence = result.Seq,
                        rawBytes = result.RawBytes
                    }).ConfigureAwait(false);
                _stateStore.Save(endpoint);
                return;
            }

            DateTimeOffset sourceTimestamp = DateTimeOffset.UtcNow;
            bool roundWasLegallyArmed =
                HasCreatedStartGame(endpoint) &&
                endpoint.RoundPhase is BridgeRoundPhases.Countdown or BridgeRoundPhases.Dealing;
            lock (_roundGate)
            {
                _publishedStartGames.Remove(BuildRoundKey(endpoint));
            }
            bool hasDeliverableStartGame = await _journal
                .HasDeliverableStartGameAsync(
                    endpoint.SourceDataCode,
                    endpoint.DeviceId,
                    endpoint.CurrentShoe,
                    endpoint.CurrentRound,
                    endpoint.CurrentRoundId)
                .ConfigureAwait(false);
            bool isDeliverableResult = IsDeliverableGameResult(result.Result);
            string p1 = ToBmsCard(endpoint.PlayerCards, 1);
            string p2 = ToBmsCard(endpoint.PlayerCards, 2);
            string p3 = ToBmsCard(endpoint.PlayerCards, 3);
            string b1 = ToBmsCard(endpoint.BankerCards, 1);
            string b2 = ToBmsCard(endpoint.BankerCards, 2);
            string b3 = ToBmsCard(endpoint.BankerCards, 3);
            bool hasMandatoryCards =
                HasMandatoryBaccaratCards(p1, p2, b1, b2);
            Guid? gameResultEventUid = endpoint.StartGameEventUid.HasValue
                ? DeriveGameResultEventUid(endpoint.StartGameEventUid.Value)
                : null;
            bool allowBmsDelivery =
                roundWasLegallyArmed &&
                hasDeliverableStartGame &&
                isDeliverableResult &&
                hasMandatoryCards;
            Log(endpoint, "EVENT", $"GameResult {endpoint.CurrentShoe}/{endpoint.CurrentRound} {result.Result} / {result.Pair}");
            await PublishBridgeEventAsync("GameResult", endpoint, new
            {
                result = result.Result,
                pair = result.Pair,
                status = string.Equals(result.Result, "ForceQuit", StringComparison.OrdinalIgnoreCase)
                    ? "Cancelled"
                    : IsNormalBaccaratResult(result.Result) ? "Normal" : "Unknown",
                sourceTimestamp = sourceTimestamp.ToString("o", CultureInfo.InvariantCulture),
                cards = new
                {
                    p1,
                    p2,
                    p3,
                    b1,
                    b2,
                    b3
                }
            }, rootFields =>
            {
                if (gameResultEventUid.HasValue)
                {
                    rootFields["eventUid"] = gameResultEventUid.Value;
                }
            }, allowBmsDelivery: allowBmsDelivery).ConfigureAwait(false);

            endpoint.MarkFinalResultStored(sourceTimestamp, result.Result);
            if (!hasMandatoryCards)
            {
                endpoint.MarkAlignmentRequired(
                    "Final result is missing one or more mandatory cards (p1, p2, b1, b2).");
            }
            _stateStore.Save(endpoint);

            if (!allowBmsDelivery)
            {
                string reason = !roundWasLegallyArmed
                    ? "本機 round phase 未合法 armed"
                    : !hasDeliverableStartGame
                        ? "無已登記 StartGame"
                        : !isDeliverableResult
                            ? $"結果 {result.Result} 不可作正常賽果"
                            : "缺少必要牌面 p1/p2/b1/b2";
                Log(endpoint, "API", $"GameResult {endpoint.CurrentShoe}/{endpoint.CurrentRound} {reason}，僅保留本機。");
            }

            if (_settings.Bridge.AutoStartNextRoundAfterResult &&
                IsNormalBaccaratResult(result.Result) &&
                hasMandatoryCards &&
                !endpoint.ShoeEnding &&
                endpoint.RoundPhase == BridgeRoundPhases.WaitingForRoundBoundary)
            {
                ScheduleNextRoundCountdownAfterResult(endpoint, sourceTimestamp);
            }
        }
        catch (Exception ex)
        {
            endpoint.MarkAlignmentRequired($"GameResult persistence failed: {ex.GetType().Name}.");
            TrySaveFailClosedState(endpoint);
            LogException(endpoint, "ERR", "GameResult handling failed", ex);
        }
    }

    private async Task EndpointCuttingCardDrawnAsync(ShoeEndpoint endpoint, SerialListener.CutCardInfo cutCard)
    {
        try
        {
            CancelPendingNextRound(endpoint);
            Log(endpoint, "EVENT", $"CutCardDrawn {endpoint.CurrentShoe}/{endpoint.CurrentRound}");
            _stateStore.Save(endpoint);
            await PublishBridgeEventAsync("CutCardDrawn", endpoint, new
            {
                shoeEnding = true,
                protocolSequence = cutCard.Seq,
                rawBytes = cutCard.RawBytes
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            endpoint.MarkAlignmentRequired($"Cut-card persistence failed: {ex.GetType().Name}.");
            TrySaveFailClosedState(endpoint);
            LogException(endpoint, "ERR", "CutCard handling failed", ex);
        }
    }

    private async Task EndpointErrorOccurredAsync(ShoeEndpoint endpoint, SerialListener.ErrorInfo error)
    {
        try
        {
            CancelPendingNextRound(endpoint);
            Log(endpoint, "EVENT", $"Error [{error.ErrorCode}] {error.ErrorMessage}");
            endpoint.MarkAlignmentRequired($"Shoe protocol error {error.ErrorCode}.");
            _stateStore.Save(endpoint);
            await PublishBridgeEventAsync("Error", endpoint, new
            {
                errorCode = error.ErrorCode,
                errorMessage = error.ErrorMessage,
                inErrorMode = error.InErrorMode,
                protocolSequence = error.Seq,
                rawBytes = error.RawBytes
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            endpoint.MarkAlignmentRequired($"Error persistence failed: {ex.GetType().Name}.");
            TrySaveFailClosedState(endpoint);
            LogException(endpoint, "ERR", "Error handling failed", ex);
        }
    }

    private async Task EndpointLockStatusChangedAsync(ShoeEndpoint endpoint, bool isLocked)
    {
        try
        {
            Log(endpoint, "EVENT", isLocked ? "LockStatus Locked" : "LockStatus Unlocked");
            await PublishBridgeEventAsync("LockStatus", endpoint, new { isLocked }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogException(endpoint, "ERR", "LockStatus handling failed", ex);
        }
    }

    private async Task EndpointErrorClearedAsync(ShoeEndpoint endpoint, int errorCode, string errorMessage)
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
            LogException(endpoint, "ERR", "ErrorCleared handling failed", ex);
        }
    }

    private void ScheduleNextRoundCountdownAfterResult(
        ShoeEndpoint endpoint,
        DateTimeOffset resultObservedAtUtc)
    {
        CancelPendingNextRound(endpoint);
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(_runCts?.Token ?? CancellationToken.None);
        lock (_roundGate)
        {
            _pendingNextRoundCountdowns[endpoint] = cts;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                int delaySeconds = _settings.Bridge.ResultToNextRoundDelaySeconds;
                Log(endpoint, "SYS", $"結算後 {delaySeconds} 秒自動進入下一局倒數。");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cts.Token).ConfigureAwait(false);
                DateTimeOffset boundaryAtUtc = DateTimeOffset.UtcNow;
                RunSerializedEndpointEvent(endpoint, async () =>
                {
                    lock (_roundGate)
                    {
                        if (!_pendingNextRoundCountdowns.TryGetValue(
                                endpoint,
                                out CancellationTokenSource? current) ||
                            !ReferenceEquals(current, cts) ||
                            cts.IsCancellationRequested)
                        {
                            return;
                        }

                        _pendingNextRoundCountdowns.Remove(endpoint);
                    }

                    bool cardArrivedBeforeBoundary =
                        endpoint.LastCardAtUtc.HasValue &&
                        endpoint.LastCardAtUtc.Value > resultObservedAtUtc;
                    if (!endpoint.IsConnected ||
                        endpoint.InErrorMode ||
                        endpoint.ShoeEnding ||
                        cardArrivedBeforeBoundary ||
                        endpoint.RoundPhase != BridgeRoundPhases.WaitingForRoundBoundary ||
                        endpoint.LastFinalResultAtUtc != resultObservedAtUtc)
                    {
                        return;
                    }

                    long previousShoe = endpoint.CurrentShoe;
                    long previousRound = endpoint.CurrentRound;
                    endpoint.BeginNextRoundCountdown();
                    bool started =
                        endpoint.CurrentShoe != previousShoe ||
                        endpoint.CurrentRound != previousRound;
                    if (!started)
                    {
                        Log(endpoint, "SYS", "下一局 boundary 未推進靴局，取消 StartGame。");
                        return;
                    }

                    endpoint.ArmRoundBoundary(
                        BridgeBoundaryStrategies.DerivedAfterPreviousResult,
                        boundaryAtUtc,
                        Guid.NewGuid());
                    endpoint.StartBetCountdown(boundaryAtUtc, endpoint.TotalBetTimeSeconds);
                    _stateStore.Save(endpoint);
                    await PublishStartGameIfNeededAsync(endpoint, boundaryAtUtc).ConfigureAwait(false);
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                endpoint.MarkAlignmentRequired($"Derived boundary failed: {ex.GetType().Name}.");
                TrySaveFailClosedState(endpoint);
                LogException(endpoint, "ERR", "Next round schedule failed", ex);
            }
            finally
            {
                lock (_roundGate)
                {
                    if (_pendingNextRoundCountdowns.TryGetValue(endpoint, out CancellationTokenSource? current) &&
                        ReferenceEquals(current, cts))
                    {
                        _pendingNextRoundCountdowns.Remove(endpoint);
                    }
                }
                cts.Dispose();
            }
        });
    }

    private void CancelPendingNextRound(ShoeEndpoint endpoint)
    {
        CancellationTokenSource? pending;
        lock (_roundGate)
        {
            _pendingNextRoundCountdowns.Remove(endpoint, out pending);
        }
        pending?.Cancel();
    }

    private async Task<bool> PublishStartGameIfNeededAsync(ShoeEndpoint endpoint, DateTimeOffset? startTimeOverride = null)
    {
        string key = BuildRoundKey(endpoint);
        lock (_roundGate)
        {
            if (_createdStartGames.Contains(key) || _creatingStartGames.Contains(key))
            {
                return _publishedStartGames.Contains(key);
            }

            if (!endpoint.StartGameEventUid.HasValue ||
                endpoint.StartGameEventUid.Value == Guid.Empty ||
                endpoint.RoundPhase != BridgeRoundPhases.Countdown)
            {
                endpoint.MarkAlignmentRequired(
                    "StartGame cannot be created without a durable trusted boundary identity.");
                _stateStore.Save(endpoint);
                return false;
            }

            _creatingStartGames.Add(key);
        }

        try
        {
            int totalBetTime = endpoint.TotalBetTimeSeconds;
            DateTimeOffset startTime = startTimeOverride ?? DateTimeOffset.UtcNow;
            Guid eventUid = endpoint.StartGameEventUid.Value;
            bool queued = await PublishBridgeEventAsync("StartGame", endpoint, new
            {
                totalBetTime,
                startTime = startTime.ToString("o", CultureInfo.InvariantCulture),
                bootId = endpoint.CurrentShoe.ToString(CultureInfo.InvariantCulture),
                groupId = 1
            }, rootFields =>
            {
                rootFields["eventUid"] = eventUid;
                rootFields["totalBetTime"] = totalBetTime;
                rootFields["startTime"] = startTime.ToString("o", CultureInfo.InvariantCulture);
            }, allowBmsDelivery: true).ConfigureAwait(false);

            endpoint.MarkStartGameStored(queued ? "Pending" : "LocalOnly");
            _stateStore.Save(endpoint);
            lock (_roundGate)
            {
                _createdStartGames.Add(key);
                if (queued)
                {
                    _publishedStartGames.Add(key);
                }
            }

            if (queued)
            {
                Log(endpoint, "EVENT", $"StartGame {endpoint.CurrentShoe}/{endpoint.CurrentRound} TotalBetTime={totalBetTime}");
            }
            else
            {
                Log(endpoint, "API", $"StartGame {endpoint.CurrentShoe}/{endpoint.CurrentRound} 僅保留本機，未進入 BMS outbox。");
            }

            return queued;
        }
        finally
        {
            lock (_roundGate)
            {
                _creatingStartGames.Remove(key);
            }
        }
    }

    private string BuildRoundKey(ShoeEndpoint endpoint) =>
        $"{endpoint.SourceDataCode}:{endpoint.CurrentShoe}:{endpoint.CurrentRound}";

    private bool HasPublishedStartGame(ShoeEndpoint endpoint)
    {
        lock (_roundGate)
        {
            return _publishedStartGames.Contains(BuildRoundKey(endpoint));
        }
    }

    private bool HasCreatedStartGame(ShoeEndpoint endpoint)
    {
        if (endpoint.RoundPhase is not (BridgeRoundPhases.Countdown or BridgeRoundPhases.Dealing))
        {
            return false;
        }

        lock (_roundGate)
        {
            return _createdStartGames.Contains(BuildRoundKey(endpoint));
        }
    }

    private void RestoreStartGameTracking(ShoeEndpoint endpoint)
    {
        if (!endpoint.StartGameEventUid.HasValue ||
            endpoint.StartGameEventUid.Value == Guid.Empty ||
            string.IsNullOrWhiteSpace(endpoint.StartGameDeliveryState) ||
            string.Equals(endpoint.StartGameDeliveryState, "Prepared", StringComparison.Ordinal))
        {
            return;
        }

        string key = BuildRoundKey(endpoint);
        lock (_roundGate)
        {
            _createdStartGames.Add(key);
            if (endpoint.StartGameDeliveryState is not ("LocalOnly" or "Rejected" or "UnregisteredSkipped"))
            {
                _publishedStartGames.Add(key);
            }
        }
    }

    private bool PersistRawFrame(ShoeEndpoint endpoint, ReadOnlySpan<byte> rawBytes)
    {
        try
        {
            if (rawBytes.IsEmpty)
            {
                return true;
            }

            string rawHex = BitConverter
                .ToString(rawBytes.ToArray())
                .Replace("-", " ", StringComparison.Ordinal);
            _rawFrameGate.Wait();
            try
            {
                _journal
                    .AppendRawFrameAsync(
                        endpoint.SourceDataCode,
                        endpoint.DeviceId,
                        endpoint.CurrentShoe,
                        endpoint.CurrentRound,
                        endpoint.CurrentRoundId,
                        "RX",
                        rawHex,
                        DateTimeOffset.UtcNow)
                    .GetAwaiter()
                    .GetResult();
            }
            finally
            {
                _rawFrameGate.Release();
            }

            return true;
        }
        catch (Exception ex)
        {
            endpoint.ExecuteSerializedStateTransition(() =>
            {
                CancelPendingNextRound(endpoint);
                endpoint.MarkAlignmentRequired($"Raw frame persistence failed: {ex.GetType().Name}.");
                TrySaveFailClosedState(endpoint);
                LogException(endpoint, "ERR", "Raw frame persistence failed", ex);
            });
            return false;
        }
    }

    private void TrySaveFailClosedState(ShoeEndpoint endpoint)
    {
        try
        {
            _stateStore.Save(endpoint);
        }
        catch (Exception stateException)
        {
            LogException(
                endpoint,
                "ERR",
                "Fail-closed state persistence failed",
                stateException);
        }
    }

    internal async Task<bool> PublishBridgeEventAsync(
        string type,
        ShoeEndpoint endpoint,
        object data,
        Action<Dictionary<string, object?>>? configureRoot = null,
        bool allowBmsDelivery = false)
    {
        bool queueForDelivery =
            allowBmsDelivery &&
            type is "StartGame" or "GameResult" &&
            IsAuthorizedBmsSender(endpoint);

        Dictionary<string, object?> payload = new()
        {
            ["bridgeId"] = _settings.Bridge.BridgeId,
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

        long eventId = await _journal.AppendAsync(payload, queueForDelivery).ConfigureAwait(false);
        string disposition = queueForDelivery ? "Outbox queued" : "Local stored";
        string eventUid = payload.TryGetValue("eventUid", out object? eventUidValue)
            ? eventUidValue?.ToString() ?? string.Empty
            : string.Empty;
        Log(
            endpoint,
            "API",
            $"{disposition} #{eventId} eventUid={eventUid} {type} {endpoint.CurrentShoe}/{endpoint.CurrentRound}");
        _ = RefreshOutboxStatusesAsync();
        return queueForDelivery;
    }

    private static string ResolveEventState(string type) => type switch
    {
        "StartGame" => "Countdown",
        "CardDrawn" => "Dealing",
        "GameResult" => "Settled",
        "CutCardDrawn" => "ShoeEnding",
        "IncompleteAtShoeChange" => "ShoeChangeIncomplete",
        "NewShoeConfirmed" => BridgeRoundPhases.ConnectedWaitingBoundary,
        "LateGameResultAfterShoeChange" => "Quarantined",
        "Error" => "Error",
        "ErrorCleared" => "Normal",
        "LockStatus" => "Locked",
        _ => "Event"
    };

    private static bool IsBaccaratCardForBms(SerialListener.CardInfo card)
    {
        return card.Target == "Player" || card.Target == "Banker";
    }

    private static bool IsAuthoritativeBaccaratCard(
        ShoeEndpoint endpoint,
        SerialListener.CardInfo card)
    {
        if (card.EventCode != 'D' ||
            card.Target is not ("Player" or "Banker") ||
            card.Index is < 1 or > 3)
        {
            return false;
        }

        IReadOnlyList<BaccaratCard> cards =
            card.Target == "Player" ? endpoint.PlayerCards : endpoint.BankerCards;
        BaccaratCard? retained = cards.FirstOrDefault(
            candidate => candidate.Index == card.Index);
        return retained is not null &&
            string.Equals(retained.Suit, card.Suit, StringComparison.Ordinal) &&
            string.Equals(retained.Value, card.Value, StringComparison.Ordinal);
    }

    private static bool IsDeliverableGameResult(string result) =>
        IsNormalBaccaratResult(result);

    private static bool HasMandatoryBaccaratCards(
        string p1,
        string p2,
        string b1,
        string b2) =>
        !string.IsNullOrWhiteSpace(p1) &&
        !string.IsNullOrWhiteSpace(p2) &&
        !string.IsNullOrWhiteSpace(b1) &&
        !string.IsNullOrWhiteSpace(b2);

    internal static Guid DeriveGameResultEventUid(Guid startGameEventUid)
    {
        if (startGameEventUid == Guid.Empty)
        {
            throw new ArgumentException(
                "StartGame eventUid must not be empty.",
                nameof(startGameEventUid));
        }

        byte[] identity = Encoding.UTF8.GetBytes(
            $"ANGEL/GameResult/{startGameEventUid:D}");
        byte[] digest = SHA256.HashData(identity);
        Guid result = new(digest.AsSpan(0, 16));
        if (result == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Derived GameResult eventUid must not be empty.");
        }

        return result;
    }

    private static bool IsNormalBaccaratResult(string result) =>
        string.Equals(result, "PlayerWin", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(result, "BankerWin", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(result, "Tie", StringComparison.OrdinalIgnoreCase);

    internal static string ToBmsCard(IEnumerable<BaccaratCard> cards, int index)
    {
        BaccaratCard? card = cards.FirstOrDefault(candidate => candidate.Index == index);
        if (card is null)
        {
            return string.Empty;
        }

        string rank = card.Value.Trim().ToUpperInvariant() switch
        {
            "1" => "A",
            "11" => "J",
            "12" => "Q",
            "13" => "K",
            string value => value
        };
        string suit = card.Suit.Trim() switch
        {
            "Spade" or "Spades" or "s" or "S" => "s",
            "Heart" or "Hearts" or "h" or "H" => "h",
            "Diamond" or "Diamonds" or "d" or "D" => "d",
            "Club" or "Clubs" or "c" or "C" => "c",
            _ => string.Empty
        };

        return string.IsNullOrWhiteSpace(rank) || string.IsNullOrWhiteSpace(suit)
            ? string.Empty
            : $"{rank}{suit}";
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
                LogException(endpoint, "WARN", "Outbox status failed", ex);
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

    internal async Task<BridgeCommandHandlingResult> HandleBmsCommandAsync(AngelBridgeCommand command, CancellationToken cancellationToken)
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
                "RecoverRound" or "ResendEvent" => BridgeCommandHandlingResult.Rejected(
                    "Legacy recovery replay through the live /events outbox is disabled; " +
                    "command-authorized recovery requires the dedicated /recoveries flow."),
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
            NextRetryUtc: null,
            Message: result.Message)).ConfigureAwait(false);
    }

    internal bool IsEventDispatchEnabled(BridgePendingEvent pending)
    {
        ShoeEndpoint? endpoint = _endpoints.FirstOrDefault(candidate =>
            string.Equals(candidate.SourceDataCode, pending.SourceDataCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.DeviceId, pending.DeviceId, StringComparison.OrdinalIgnoreCase));

        return IsAuthorizedBmsSender(endpoint);
    }

    internal bool HasAuthorizedBmsSender() => _endpoints.Any(IsAuthorizedBmsSender);

    internal static bool IsAuthorizedBmsSender(ShoeEndpoint? endpoint) =>
        endpoint is { Enabled: true, BmsTransmitEnabled: true };

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
            LogException(null, "ERR", "Query request failed", ex);
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

    private void LogException(
        ShoeEndpoint? endpoint,
        string type,
        string context,
        Exception exception)
    {
        Log(
            endpoint,
            type,
            $"{context}: {BridgeDiagnosticFormatter.FormatException(exception)}");
    }
}
