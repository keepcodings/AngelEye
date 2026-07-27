using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AngelEyeBmsBridge;

/// <summary>
/// Sends bridge events from the local SQLite outbox to the configured BMS event API.
/// </summary>
public sealed class BmsApiClient : IDisposable
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BusyDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan DefaultRecoveryPollDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MinRecoveryPollDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRecoveryPollDelay = TimeSpan.FromMinutes(5);
    private static readonly Regex RecoveryCommandIdPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9:._-]{0,127}$",
        RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private BmsApiSettings _settings = BmsApiSettings.Empty;
    private BridgeEventJournal? _journal;
    private Func<BridgePendingEvent, bool> _canDispatchEvent = _ => true;
    private Func<IReadOnlyList<AngelBridgeHeartbeatEndpointStatus>>? _heartbeatSnapshotProvider;
    private Func<AngelBridgeCommand, CancellationToken, Task<BridgeCommandHandlingResult>>? _commandHandler;
    private Func<string>? _bearerTokenProvider;
    private IBmsAccessTokenProvider? _accessTokenProvider;
    private CancellationTokenSource? _dispatcherCancellation;
    private Task? _dispatcherTask;
    private Task? _heartbeatTask;
    private int _recoveryFailureCount;

    /// <summary>Creates a BMS client with the default HTTP transport.</summary>
    public BmsApiClient()
        : this(CreateSecureHttpHandler())
    {
    }

    internal BmsApiClient(HttpMessageHandler handler)
    {
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
    }

    internal static HttpClientHandler CreateSecureHttpHandler() => new()
    {
        AllowAutoRedirect = false
    };

    /// <summary>Raised when the dispatcher has a human-readable log message.</summary>
    public event Action<string>? OnLogReceived;

    /// <summary>Raised when the API dispatcher status changes.</summary>
    public event Action<string>? OnStatusChanged;

    /// <summary>Gets whether the background outbox dispatcher is running.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>
    /// Starts the background dispatcher for pending events in the journal.
    /// </summary>
    /// <param name="settings">Target API settings.</param>
    /// <param name="journal">Local bridge event journal used as the outbox.</param>
    /// <param name="canDispatchEvent">Optional predicate used to pause delivery for specific endpoints.</param>
    /// <param name="heartbeatSnapshotProvider">Optional provider that creates the heartbeat endpoint snapshot.</param>
    /// <param name="commandHandler">Optional handler for commands returned by BMS heartbeat responses.</param>
    /// <param name="accessTokenProvider">Required short-lived access-token provider for client credentials.</param>
    public void Start(
        BmsApiSettings settings,
        BridgeEventJournal journal,
        Func<BridgePendingEvent, bool>? canDispatchEvent = null,
        Func<IReadOnlyList<AngelBridgeHeartbeatEndpointStatus>>? heartbeatSnapshotProvider = null,
        Func<AngelBridgeCommand, CancellationToken, Task<BridgeCommandHandlingResult>>? commandHandler = null,
        IBmsAccessTokenProvider? accessTokenProvider = null)
    {
        Uri uri = NormalizeUrl(settings.Url);
        if (accessTokenProvider is null)
        {
            throw new InvalidOperationException(
                "Production BMS dispatch requires the client-credentials access-token provider.");
        }

        _settings = settings with
        {
            Url = uri.ToString(),
            Token = string.Empty
        };
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _canDispatchEvent = canDispatchEvent ?? (_ => true);
        _heartbeatSnapshotProvider = heartbeatSnapshotProvider;
        _commandHandler = commandHandler;
        _bearerTokenProvider = null;
        _accessTokenProvider = accessTokenProvider;
        IsRunning = true;
        OnStatusChanged?.Invoke("傳送中");
        OnLogReceived?.Invoke($"事件 API 傳送已開始: {uri}，SQLite Outbox 已接管送出。");

        _dispatcherCancellation = new CancellationTokenSource();
        _dispatcherTask = Task.Run(() => DispatchLoopAsync(_dispatcherCancellation.Token));
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_dispatcherCancellation.Token));
    }

    internal Task<TimeSpan> RunRecoveryCheckOnceAsync(
        BmsApiSettings settings,
        BridgeEventJournal journal,
        Func<BridgePendingEvent, bool> canDispatchEvent,
        Func<IReadOnlyList<AngelBridgeHeartbeatEndpointStatus>> heartbeatSnapshotProvider,
        CancellationToken cancellationToken = default,
        Func<string>? bearerTokenProvider = null,
        IBmsAccessTokenProvider? accessTokenProvider = null)
    {
        EnsureOnlyOneTokenProvider(bearerTokenProvider, accessTokenProvider);
        _settings = settings;
        _journal = journal;
        _canDispatchEvent = canDispatchEvent;
        _heartbeatSnapshotProvider = heartbeatSnapshotProvider;
        _bearerTokenProvider = bearerTokenProvider;
        _accessTokenProvider = accessTokenProvider;
        return SendHeartbeatAsync(cancellationToken);
    }

    internal Task<int> RunDispatchOnceAsync(
        BmsApiSettings settings,
        BridgeEventJournal journal,
        Func<BridgePendingEvent, bool> canDispatchEvent,
        CancellationToken cancellationToken = default,
        Func<string>? bearerTokenProvider = null,
        IBmsAccessTokenProvider? accessTokenProvider = null)
    {
        EnsureOnlyOneTokenProvider(bearerTokenProvider, accessTokenProvider);
        _settings = settings;
        _journal = journal;
        _canDispatchEvent = canDispatchEvent;
        _bearerTokenProvider = bearerTokenProvider;
        _accessTokenProvider = accessTokenProvider;
        return DispatchPendingAsync(cancellationToken);
    }

    /// <summary>
    /// Stops the background dispatcher and waits for the current send cycle to finish.
    /// </summary>
    /// <returns>A task that completes after the dispatcher stops.</returns>
    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        _dispatcherCancellation?.Cancel();
        if (_dispatcherTask != null)
        {
            try
            {
                await _dispatcherTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_heartbeatTask != null)
        {
            try
            {
                await _heartbeatTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _dispatcherCancellation?.Dispose();
        _dispatcherCancellation = null;
        _dispatcherTask = null;
        _heartbeatTask = null;
        _recoveryFailureCount = 0;
        _heartbeatSnapshotProvider = null;
        _commandHandler = null;
        _bearerTokenProvider = null;
        _accessTokenProvider = null;
        OnStatusChanged?.Invoke("未開始");
        OnLogReceived?.Invoke("事件 API 傳送已停止。");
    }

    private async Task DispatchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                int processed = await DispatchPendingAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(processed > 0 ? BusyDelay : IdleDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                OnLogReceived?.Invoke(
                    $"Outbox dispatcher error: {BridgeDiagnosticFormatter.FormatException(ex)}");
                await Task.Delay(IdleDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        BridgeEventJournal? journal = _journal;
        if (journal == null)
        {
            return 0;
        }

        Uri uri;
        try
        {
            uri = NormalizeUrl(_settings.Url);
        }
        catch (Exception ex)
        {
            OnLogReceived?.Invoke(
                $"事件 API URL 無效: {BridgeDiagnosticFormatter.FormatException(ex)}");
            return 0;
        }

        List<BridgePendingEvent> events = await journal.GetDueOutboxEventsAsync(20, DateTime.UtcNow).ConfigureAwait(false);
        int dispatched = 0;
        foreach (BridgePendingEvent pending in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_canDispatchEvent(pending))
            {
                continue;
            }

            if (string.Equals(pending.Type, "GameResult", StringComparison.Ordinal) &&
                !await journal
                    .PrepareGameResultForDeliveryAsync(pending.EventId)
                    .ConfigureAwait(false))
            {
                continue;
            }

            DateTime claimAt = DateTime.UtcNow;
            if (!await journal
                    .TryClaimForDeliveryAsync(pending.EventId, claimAt)
                    .ConfigureAwait(false))
            {
                continue;
            }

            if (!BridgeEventUidValidator.TryValidate(pending, out string identityError))
            {
                DateTime failedAt = DateTime.UtcNow;
                int retryCount = pending.RetryCount + 1;
                await journal
                    .MarkRejectedAsync(pending.EventId, retryCount, failedAt, identityError)
                    .ConfigureAwait(false);
                OnLogReceived?.Invoke(
                    $"{BuildEventLabel(pending)} 未送出: {identityError}，已 fail-closed。");
                dispatched++;
                continue;
            }

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                BridgeSendResult result = await SendJsonAsync(
                        uri,
                        pending.PayloadJson,
                        pending.EventId,
                        pending.EventUid,
                        cancellationToken)
                    .ConfigureAwait(false);
                DateTime now = DateTime.UtcNow;
                string eventLabel = BuildEventLabel(pending);
                if (result.Success)
                {
                    await journal.MarkSentAsync(pending.EventId, now, result.StatusCode).ConfigureAwait(false);
                    OnLogReceived?.Invoke($"POST {eventLabel} -> {result.StatusCode}，已標記送達。");
                }
                else
                {
                    int retryCount = pending.RetryCount + 1;
                    if (result.DefinitivelyRejected)
                    {
                        await journal
                            .MarkRejectedAsync(pending.EventId, retryCount, now, result.Error, result.StatusCode)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await journal
                            .MarkUnconfirmedAsync(pending.EventId, retryCount, now, result.Error, result.StatusCode)
                            .ConfigureAwait(false);
                    }
                    OnLogReceived?.Invoke(
                        result.DefinitivelyRejected
                            ? $"POST {eventLabel} 拒絕: {result.Error}，已標記 Rejected，不會送出同局賽果。"
                            : $"POST {eventLabel} ACK 不明: {result.Error}，已保留為 Unconfirmed，不會自動重送。");
                }
            }
            finally
            {
                _sendLock.Release();
            }

            dispatched++;
        }

        return dispatched;
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan delay = DefaultRecoveryPollDelay;
            try
            {
                delay = await SendHeartbeatAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _recoveryFailureCount++;
                delay = CalculateRecoveryErrorDelay(_recoveryFailureCount);
                OnLogReceived?.Invoke(
                    $"BMS 補償查詢 error: {BridgeDiagnosticFormatter.FormatException(ex)}，{delay.TotalSeconds:0} 秒後重試。");
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TimeSpan> SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        if (_heartbeatSnapshotProvider == null)
        {
            return DefaultRecoveryPollDelay;
        }

        Uri recoveryUri;
        try
        {
            recoveryUri = BuildSiblingEndpoint(_settings.Url, "recoveries/check");
        }
        catch (Exception ex)
        {
            OnLogReceived?.Invoke(
                $"BMS 補償查詢 URL 無效: {BridgeDiagnosticFormatter.FormatException(ex)}");
            return DefaultRecoveryPollDelay;
        }

        DateTimeOffset sentAt = DateTimeOffset.UtcNow;
        List<BridgeRecoveryCandidate> candidates = _journal == null
            ? []
            : await _journal
                .GetDueRecoveryCandidatesAsync(20, sentAt)
                .ConfigureAwait(false);
        List<BridgeRecoveryCandidate> authorizedCandidates = candidates
            .Where(candidate => _canDispatchEvent(ToPendingIdentity(candidate)))
            .Take(20)
            .ToList();
        AngelBridgeHeartbeatRequest heartbeat = new(
            BridgeId: ResolveBridgeId(),
            BridgeName: ResolveBridgeName(),
            Version: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            MachineName: Environment.MachineName,
            SentAt: sentAt,
            Endpoints: _heartbeatSnapshotProvider(),
            Environment: _settings.Environment,
            UnconfirmedEvents: authorizedCandidates.Select(ToUnconfirmedSummary).ToList());

        string json = JsonSerializer.Serialize(heartbeat, JsonOptions);
        string correlationId = $"angel-poll-{Guid.NewGuid():N}";
        AuthenticatedPostResponse authenticated = await SendAuthenticatedPostAsync(
                recoveryUri,
                json,
                correlationId,
                cancellationToken)
            .ConfigureAwait(false);
        using HttpResponseMessage response = authenticated.Response;
        string responseText = authenticated.Body;
        if (!response.IsSuccessStatusCode)
        {
            _recoveryFailureCount++;
            TimeSpan retryDelay = CalculateRecoveryErrorDelay(_recoveryFailureCount);
            OnLogReceived?.Invoke(
                $"BMS 補償查詢 correlationId={correlationId} -> {(int)response.StatusCode} {response.ReasonPhrase}，{retryDelay.TotalSeconds:0} 秒後重試。");
            return retryDelay;
        }

        BmsResponseEnvelope<AngelBridgeHeartbeatResponse>? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<BmsResponseEnvelope<AngelBridgeHeartbeatResponse>>(responseText, JsonOptions);
        }
        catch (JsonException ex)
        {
            _recoveryFailureCount++;
            TimeSpan retryDelay = CalculateRecoveryErrorDelay(_recoveryFailureCount);
            OnLogReceived?.Invoke(
                $"BMS 補償查詢 correlationId={correlationId} ACK JSON 無法解析: {BridgeDiagnosticFormatter.FormatException(ex)}，{retryDelay.TotalSeconds:0} 秒後重試。");
            return retryDelay;
        }

        if (envelope == null || envelope.ErrCode != 0 || envelope.Data?.Accepted != true)
        {
            _recoveryFailureCount++;
            TimeSpan retryDelay = CalculateRecoveryErrorDelay(_recoveryFailureCount);
            OnLogReceived?.Invoke(
                $"BMS 補償查詢 correlationId={correlationId} ACK rejected {TrimForLog(responseText)}，{retryDelay.TotalSeconds:0} 秒後重試。");
            return retryDelay;
        }

        _recoveryFailureCount = 0;
        AngelBridgeHeartbeatResponse heartbeatResponse = envelope.Data!;
        TimeSpan nextDelay = ResolveNextRecoveryDelay(heartbeatResponse);
        if (heartbeatResponse.RateLimited)
        {
            OnLogReceived?.Invoke($"BMS 補償查詢已節流，{nextDelay.TotalSeconds:0} 秒後再查。");
        }

        await ProcessRecoveryResponseAsync(
                heartbeatResponse,
                authorizedCandidates,
                cancellationToken)
            .ConfigureAwait(false);

        return nextDelay;
    }

    private async Task ProcessRecoveryResponseAsync(
        AngelBridgeHeartbeatResponse response,
        IReadOnlyList<BridgeRecoveryCandidate> submitted,
        CancellationToken cancellationToken)
    {
        BridgeEventJournal? journal = _journal;
        if (journal == null)
        {
            return;
        }

        Dictionary<long, BridgeRecoveryCandidate> byEventId = submitted.ToDictionary(candidate => candidate.EventId);
        HashSet<long> explicitlyHandled = [];
        foreach (AngelBridgeRecoveryDecision decision in response.Decisions.Take(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryMatchDecision(decision, byEventId, out BridgeRecoveryCandidate? candidate, out string error))
            {
                if (candidate != null)
                {
                    explicitlyHandled.Add(candidate.EventId);
                    await journal
                        .ApplyRecoveryDecisionAsync(
                            candidate,
                            "Conflict",
                            string.Empty,
                            error,
                            DateTimeOffset.UtcNow)
                        .ConfigureAwait(false);
                    await RecordDecisionAuditAsync(
                            candidate,
                            "Conflict",
                            string.Empty,
                            error,
                            decision.Generation,
                            decision.DispatchCount)
                        .ConfigureAwait(false);
                }

                OnLogReceived?.Invoke(
                    $"BMS recovery decision rejected: eventId={candidate?.EventId}, eventUid={candidate?.EventUid}, generation={decision.Generation}, dispatchCount={decision.DispatchCount}, reason={error}");
                continue;
            }

            BridgeRecoveryCandidate exactCandidate = candidate!;
            explicitlyHandled.Add(exactCandidate.EventId);
            string normalizedDecision = NormalizeDecision(decision.Decision);
            if (normalizedDecision.Length == 0)
            {
                const string unsupported = "Unsupported BMS recovery decision.";
                await journal
                    .ApplyRecoveryDecisionAsync(
                        exactCandidate,
                        "Conflict",
                        string.Empty,
                        unsupported,
                        DateTimeOffset.UtcNow)
                    .ConfigureAwait(false);
                await RecordDecisionAuditAsync(
                        exactCandidate,
                        "Conflict",
                        string.Empty,
                        unsupported,
                        decision.Generation,
                        decision.DispatchCount)
                    .ConfigureAwait(false);
                continue;
            }

            bool requiresCommandLedger = IsCommandTerminalDecision(normalizedDecision);
            bool commandScopedConflict =
                normalizedDecision == "Conflict" &&
                HasAnyCommandMetadata(decision);
            if ((requiresCommandLedger || commandScopedConflict) &&
                (!HasCompleteCommandMetadata(decision) ||
                 !await journal
                     .IsExactLatestRecoveryCommandAsync(
                         exactCandidate,
                         decision.CommandId,
                         decision.Generation,
                         decision.DispatchCount)
                     .ConfigureAwait(false)))
            {
                await journal
                    .RecordMissingRecoveryDecisionAsync(
                        exactCandidate,
                        DateTimeOffset.UtcNow)
                    .ConfigureAwait(false);
                OnLogReceived?.Invoke(
                    $"BMS terminal recovery decision rejected: commandId={decision.CommandId}, eventId={exactCandidate.EventId}, eventUid={exactCandidate.EventUid}, generation={decision.Generation}, dispatchCount={decision.DispatchCount}; command generation or dispatch does not match the latest local ledger.");
                continue;
            }

            if (normalizedDecision == "RecoverRound")
            {
                AngelBridgeCommand command = ToRecoveryCommand(decision);
                BridgeCommandHandlingResult result = await ExecuteRecoveryCommandAsync(
                        command,
                        cancellationToken)
                    .ConfigureAwait(false);
                LogCommandResult(command, result);
                continue;
            }

            await journal
                .ApplyRecoveryDecisionAsync(
                    exactCandidate,
                    normalizedDecision,
                    string.Empty,
                    decision.Message,
                    DateTimeOffset.UtcNow)
                .ConfigureAwait(false);
            await RecordDecisionAuditAsync(
                    exactCandidate,
                    normalizedDecision,
                    decision.CommandId,
                    decision.Message,
                    decision.Generation,
                    decision.DispatchCount)
                .ConfigureAwait(false);
        }

        foreach (AngelBridgeCommand command in response.Commands.Take(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command.EventId.HasValue && explicitlyHandled.Contains(command.EventId.Value))
            {
                continue;
            }

            BridgeCommandHandlingResult result;
            if (string.Equals(command.Type.Trim(), "RecoverRound", StringComparison.Ordinal))
            {
                result = await ExecuteRecoveryCommandAsync(command, cancellationToken).ConfigureAwait(false);
                if (command.EventId.HasValue &&
                    byEventId.ContainsKey(command.EventId.Value))
                {
                    explicitlyHandled.Add(command.EventId.Value);
                }
            }
            else
            {
                result = await HandleCommandAsync(command, cancellationToken).ConfigureAwait(false);
            }

            LogCommandResult(command, result);
        }

        foreach (BridgeRecoveryCandidate candidate in submitted.Where(candidate =>
                     !explicitlyHandled.Contains(candidate.EventId)))
        {
            await journal
                .RecordMissingRecoveryDecisionAsync(candidate, DateTimeOffset.UtcNow)
                .ConfigureAwait(false);
        }
    }

    private async Task<BridgeCommandHandlingResult> ExecuteRecoveryCommandAsync(
        AngelBridgeCommand command,
        CancellationToken cancellationToken)
    {
        BridgeEventJournal? journal = _journal;
        if (journal == null)
        {
            return BridgeCommandHandlingResult.Rejected("Recovery journal is not configured.");
        }

        if (!TryValidateRecoveryCommand(command, out string validationError))
        {
            await RecordCommandAuditAsync(command, "Conflict", validationError).ConfigureAwait(false);
            return BridgeCommandHandlingResult.Rejected(validationError);
        }

        BridgePendingEvent commandIdentity = new(
            command.EventId!.Value,
            "GameResult",
            command.SourceDataCode,
            command.DeviceId,
            command.Shoe!.Value,
            command.Round!.Value,
            PayloadJson: string.Empty,
            RetryCount: 0,
            command.EventUid);
        if (!_canDispatchEvent(commandIdentity))
        {
            const string disabled = "Recovery transmission is disabled for the exact endpoint.";
            await RecordCommandAuditAsync(command, "Rejected", disabled).ConfigureAwait(false);
            return BridgeCommandHandlingResult.Rejected(disabled);
        }

        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        BridgeRecoveryAudit requestAudit = BuildCommandAudit(
            command,
            "RecoveryRequested",
            "Authorized recovery dispatch received.",
            observedAt);
        BridgeRecoveryReservationResult reservation = await journal
            .ReserveRecoveryCommandAsync(requestAudit)
            .ConfigureAwait(false);
        if (reservation.Disposition == BridgeRecoveryReservationDisposition.Duplicate)
        {
            return BridgeCommandHandlingResult.Handled(
                "This command dispatch was already observed; no duplicate recovery POST was made.");
        }

        if (reservation.Disposition == BridgeRecoveryReservationDisposition.Conflict)
        {
            await RecordReservationConflictAuditAsync(
                    command,
                    reservation,
                    observedAt)
                .ConfigureAwait(false);
            return BridgeCommandHandlingResult.Rejected(
                $"Recovery command conflict: {reservation.Message}");
        }

        BridgeRecoveryLookupResult lookup = await journal
            .LookupRecoveryGameResultAsync(
                command.EventId!.Value,
                command.EventUid,
                command.SourceDataCode,
                command.DeviceId,
                command.Shoe!.Value,
                command.Round!.Value,
                command.RoundId)
            .ConfigureAwait(false);
        if (lookup.Disposition == "Conflict")
        {
            return await SubmitRecoveryConflictAsync(
                    command,
                    retained: null,
                    lookup.Message,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        BridgePendingEvent? retained = lookup.Event;
        JsonElement? gameResult = null;
        string submissionOutcome = "NotFound";
        if (retained != null)
        {
            if (!await journal
                    .MarkRecoveryRequestedAsync(retained.EventId, retained.EventUid, command.CommandId)
                    .ConfigureAwait(false))
            {
                const string stateConflict =
                    "Retained event is not in an authorized recoverable reconciliation state.";
                return await SubmitRecoveryConflictAsync(
                        command,
                        retained,
                        stateConflict,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!TryReadRetainedGameResult(
                    retained,
                    command,
                    ResolveBridgeId(),
                    out JsonElement retainedResult,
                    out string payloadError))
            {
                return await SubmitRecoveryConflictAsync(
                        command,
                        retained,
                        payloadError,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            submissionOutcome = "Found";
            gameResult = retainedResult;
        }

        return await SubmitRecoveryOutcomeAsync(
                command,
                retained,
                submissionOutcome,
                gameResult,
                message: string.Empty,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task<BridgeCommandHandlingResult> SubmitRecoveryConflictAsync(
        AngelBridgeCommand command,
        BridgePendingEvent? retained,
        string message,
        CancellationToken cancellationToken) =>
        SubmitRecoveryOutcomeAsync(
            command,
            retained,
            "Conflict",
            gameResult: null,
            message,
            cancellationToken);

    private async Task<BridgeCommandHandlingResult> SubmitRecoveryOutcomeAsync(
        AngelBridgeCommand command,
        BridgePendingEvent? retained,
        string outcome,
        JsonElement? gameResult,
        string message,
        CancellationToken cancellationToken)
    {
        AngelBridgeRecoverySubmission submission = new()
        {
            CommandId = command.CommandId,
            Generation = command.Generation,
            DispatchCount = command.DispatchCount,
            BridgeId = ResolveBridgeId(),
            SourceDataCode = command.SourceDataCode,
            DeviceId = command.DeviceId,
            Shoe = command.Shoe!.Value,
            Round = command.Round!.Value,
            RoundId = command.RoundId,
            EventId = command.EventId!.Value,
            EventUid = command.EventUid,
            Outcome = outcome,
            GameResult = gameResult,
            Message = string.IsNullOrWhiteSpace(message) ? null : message
        };
        RecoveryPostResult postResult = await SendRecoverySubmissionAsync(
                submission,
                cancellationToken)
            .ConfigureAwait(false);

        string localOutcome = postResult.Outcome;
        BridgeEventJournal? journal = _journal;
        if (retained != null && journal != null)
        {
            await journal
                .MarkRecoverySubmissionOutcomeAsync(
                    retained.EventId,
                    retained.EventUid,
                    command.CommandId,
                    localOutcome,
                    postResult.Message,
                    DateTimeOffset.UtcNow)
                .ConfigureAwait(false);
        }

        await RecordCommandAuditAsync(command, localOutcome, postResult.Message).ConfigureAwait(false);
        return postResult.Success
            ? BridgeCommandHandlingResult.Handled(postResult.Message)
            : postResult.Outcome == "NotFound"
                ? BridgeCommandHandlingResult.NotFound(postResult.Message)
                : BridgeCommandHandlingResult.Rejected(postResult.Message);
    }

    private async Task<RecoveryPostResult> SendRecoverySubmissionAsync(
        AngelBridgeRecoverySubmission submission,
        CancellationToken cancellationToken)
    {
        Uri uri = BuildSiblingEndpoint(_settings.Url, "recoveries");
        string json = JsonSerializer.Serialize(submission, JsonOptions);
        try
        {
            AuthenticatedPostResponse authenticated = await SendAuthenticatedPostAsync(
                    uri,
                    json,
                    submission.CommandId,
                    cancellationToken)
                .ConfigureAwait(false);
            using HttpResponseMessage response = authenticated.Response;
            string responseText = authenticated.Body;
            if (!response.IsSuccessStatusCode)
            {
                int statusCode = (int)response.StatusCode;
                string detail = $"{statusCode} {response.ReasonPhrase} {TrimForLog(responseText)}";
                return statusCode == 409
                    ? RecoveryPostResult.Failed("Conflict", detail)
                    : IsDefinitiveHttpRejection(statusCode)
                        ? RecoveryPostResult.Failed("Rejected", detail)
                        : RecoveryPostResult.Failed("RecoveryUnconfirmed", detail);
            }

            BmsResponseEnvelope<AngelBridgeRecoveryAcknowledgement>? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<
                    BmsResponseEnvelope<AngelBridgeRecoveryAcknowledgement>>(
                    responseText,
                    JsonOptions);
            }
            catch (JsonException)
            {
                return RecoveryPostResult.Failed(
                    "RecoveryUnconfirmed",
                    $"Recovery ACK missing or invalid {TrimForLog(responseText)}");
            }

            AngelBridgeRecoveryAcknowledgement? acknowledgement = envelope?.Data;
            bool exactIdentity =
                envelope?.ErrCode == 0 &&
                acknowledgement?.Accepted == true &&
                string.Equals(
                    acknowledgement.CommandId,
                    submission.CommandId,
                    StringComparison.Ordinal) &&
                acknowledgement.Generation == submission.Generation &&
                acknowledgement.DispatchCount == submission.DispatchCount &&
                string.Equals(
                    acknowledgement.EventUid,
                    submission.EventUid,
                    StringComparison.OrdinalIgnoreCase);
            if (!exactIdentity)
            {
                return RecoveryPostResult.Failed(
                    "RecoveryUnconfirmed",
                    $"Recovery ACK identity missing or invalid {TrimForLog(responseText)}");
            }

            string ackOutcome = acknowledgement!.Outcome.Trim();
            bool validOutcome = submission.Outcome switch
            {
                "NotFound" => ackOutcome == "NotFound",
                "Conflict" => ackOutcome == "Conflict",
                _ => ackOutcome is "Recovered" or "AlreadyAccepted" or "Duplicate"
            };
            if (!validOutcome)
            {
                return RecoveryPostResult.Failed(
                    "RecoveryUnconfirmed",
                    $"Recovery ACK outcome missing or invalid {TrimForLog(responseText)}");
            }

            string localOutcome = ackOutcome == "Duplicate" ? "Recovered" : ackOutcome;
            string message = string.IsNullOrWhiteSpace(acknowledgement.Message)
                ? $"Recovery acknowledged as {ackOutcome}."
                : acknowledgement.Message;
            return RecoveryPostResult.Succeeded(localOutcome, message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return RecoveryPostResult.Failed(
                "RecoveryUnconfirmed",
                BridgeDiagnosticFormatter.FormatException(ex));
        }
    }

    private async Task RecordDecisionAuditAsync(
        BridgeRecoveryCandidate candidate,
        string decision,
        string commandId,
        string message,
        int generation,
        int dispatchCount)
    {
        if (_journal == null)
        {
            return;
        }

        string auditId = string.IsNullOrWhiteSpace(commandId)
            ? $"decision:{candidate.EventUid}:{decision}"
            : commandId;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await _journal.RecordRecoveryRequestAsync(new BridgeRecoveryAudit(
            auditId,
            decision,
            candidate.SourceDataCode,
            candidate.DeviceId,
            candidate.Shoe,
            candidate.Round,
            candidate.RoundId,
            now,
            now,
            decision,
            NextRetryUtc: null,
            Message: message,
            EventId: candidate.EventId,
            EventUid: candidate.EventUid,
            Outcome: decision,
            DecisionCount: candidate.DecisionCount + 1,
            TerminalReason: IsTerminalDecision(decision) ? message : string.Empty,
            Generation: generation,
            DispatchCount: dispatchCount)).ConfigureAwait(false);
    }

    private async Task RecordCommandAuditAsync(
        AngelBridgeCommand command,
        string result,
        string message)
    {
        if (_journal == null)
        {
            return;
        }

        await _journal
            .RecordRecoveryRequestAsync(BuildCommandAudit(
                command,
                result,
                message,
                DateTimeOffset.UtcNow))
            .ConfigureAwait(false);
    }

    private async Task RecordReservationConflictAuditAsync(
        AngelBridgeCommand command,
        BridgeRecoveryReservationResult reservation,
        DateTimeOffset observedAt)
    {
        if (_journal == null)
        {
            return;
        }

        BridgeRecoveryAudit audit = BuildCommandAudit(
            command,
            "Conflict",
            reservation.Message,
            observedAt) with
        {
            CommandId = reservation.CommandIdAlreadyExists
                ? $"reservation-conflict:{command.CommandId}:g{command.Generation}:d{command.DispatchCount}"
                : command.CommandId,
            CommandType = "RecoverRoundConflict"
        };
        await _journal.RecordRecoveryRequestAsync(audit).ConfigureAwait(false);
    }

    private static BridgeRecoveryAudit BuildCommandAudit(
        AngelBridgeCommand command,
        string result,
        string message,
        DateTimeOffset observedAt) => new(
        command.CommandId,
        "RecoverRound",
        command.SourceDataCode,
        command.DeviceId,
        command.Shoe,
        command.Round,
        command.RoundId,
        observedAt,
        observedAt,
        result,
        NextRetryUtc: null,
        Message: message,
        EventId: command.EventId,
        EventUid: command.EventUid,
        Outcome: result,
        DecisionCount: 1,
        TerminalReason: IsTerminalRecoveryOutcome(result) ? message : string.Empty,
        Generation: command.Generation,
        DispatchCount: command.DispatchCount);

    private static bool TryReadRetainedGameResult(
        BridgePendingEvent pending,
        AngelBridgeCommand command,
        string expectedBridgeId,
        out JsonElement gameResult,
        out string error)
    {
        if (!BridgeEventUidValidator.TryValidate(pending, out string eventUidError))
        {
            gameResult = default;
            error = $"Retained GameResult identity is invalid: {eventUidError}.";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(pending.PayloadJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetPropertyIgnoreCase(root, "type", out JsonElement typeElement) ||
                !string.Equals(typeElement.GetString(), "GameResult", StringComparison.Ordinal) ||
                !TryGetPropertyIgnoreCase(root, "data", out JsonElement dataElement) ||
                dataElement.ValueKind != JsonValueKind.Object)
            {
                gameResult = default;
                error = "Retained recovery payload is not an exact GameResult.";
                return false;
            }

            bool exactIdentity =
                !string.IsNullOrWhiteSpace(expectedBridgeId) &&
                TryGetPropertyIgnoreCase(root, "bridgeId", out JsonElement bridgeIdElement) &&
                bridgeIdElement.ValueKind == JsonValueKind.String &&
                string.Equals(
                    bridgeIdElement.GetString(),
                    expectedBridgeId,
                    StringComparison.Ordinal) &&
                TryGetPropertyIgnoreCase(root, "eventId", out JsonElement eventIdElement) &&
                eventIdElement.TryGetInt64(out long payloadEventId) &&
                payloadEventId == pending.EventId &&
                TryGetPropertyIgnoreCase(root, "eventUid", out JsonElement eventUidElement) &&
                eventUidElement.ValueKind == JsonValueKind.String &&
                string.Equals(
                    eventUidElement.GetString(),
                    command.EventUid,
                    StringComparison.OrdinalIgnoreCase) &&
                TryGetPropertyIgnoreCase(root, "sourceDataCode", out JsonElement sourceCodeElement) &&
                sourceCodeElement.ValueKind == JsonValueKind.String &&
                string.Equals(
                    sourceCodeElement.GetString(),
                    command.SourceDataCode,
                    StringComparison.OrdinalIgnoreCase) &&
                TryGetPropertyIgnoreCase(root, "deviceId", out JsonElement deviceIdElement) &&
                deviceIdElement.ValueKind == JsonValueKind.String &&
                string.Equals(
                    deviceIdElement.GetString(),
                    command.DeviceId,
                    StringComparison.Ordinal) &&
                TryGetPropertyIgnoreCase(root, "shoe", out JsonElement shoeElement) &&
                shoeElement.TryGetInt64(out long payloadShoe) &&
                payloadShoe == command.Shoe &&
                TryGetPropertyIgnoreCase(root, "round", out JsonElement roundElement) &&
                roundElement.TryGetInt64(out long payloadRound) &&
                payloadRound == command.Round &&
                TryGetPropertyIgnoreCase(root, "roundId", out JsonElement roundIdElement) &&
                roundIdElement.TryGetInt64(out long payloadRoundId) &&
                payloadRoundId == command.RoundId;
            if (!exactIdentity)
            {
                gameResult = default;
                error = "Retained GameResult payload identity does not exactly match the authorized command.";
                return false;
            }

            gameResult = dataElement.Clone();
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            gameResult = default;
            error =
                $"Retained GameResult JSON is invalid: {BridgeDiagnosticFormatter.FormatException(ex)}";
            return false;
        }
    }

    private static bool TryMatchDecision(
        AngelBridgeRecoveryDecision decision,
        IReadOnlyDictionary<long, BridgeRecoveryCandidate> submitted,
        out BridgeRecoveryCandidate? candidate,
        out string error)
    {
        candidate = decision.EventId.HasValue &&
            submitted.TryGetValue(decision.EventId.Value, out BridgeRecoveryCandidate? matched)
                ? matched
                : null;
        if (candidate == null)
        {
            error = "Decision does not identify a submitted eventId.";
            return false;
        }

        bool exact =
            Guid.TryParse(decision.EventUid, out Guid decisionUid) &&
            decisionUid != Guid.Empty &&
            string.Equals(candidate.EventUid, decision.EventUid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.SourceDataCode, decision.SourceDataCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.DeviceId, decision.DeviceId, StringComparison.OrdinalIgnoreCase) &&
            decision.Shoe == candidate.Shoe &&
            decision.Round == candidate.Round &&
            decision.RoundId.HasValue &&
            decision.RoundId == candidate.RoundId;
        if (!exact)
        {
            error = "Decision identity does not exactly match the submitted event.";
            return false;
        }

        if (string.Equals(decision.Decision.Trim(), "RecoverRound", StringComparison.Ordinal) &&
            (!string.Equals(candidate.EventType, "GameResult", StringComparison.Ordinal) ||
             !IsValidRecoveryCommandId(decision.CommandId) ||
             decision.Generation <= 0 ||
             decision.DispatchCount <= 0))
        {
            error =
                "RecoverRound requires an exact GameResult identity and a valid commandId, generation, and dispatchCount.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryValidateRecoveryCommand(
        AngelBridgeCommand command,
        out string error)
    {
        bool valid =
            string.Equals(command.Type.Trim(), "RecoverRound", StringComparison.Ordinal) &&
            IsValidRecoveryCommandId(command.CommandId) &&
            command.EventId is > 0 &&
            Guid.TryParse(command.EventUid, out Guid eventUid) &&
            eventUid != Guid.Empty &&
            !string.IsNullOrWhiteSpace(command.SourceDataCode) &&
            !string.IsNullOrWhiteSpace(command.DeviceId) &&
            command.Shoe is > 0 &&
            command.Round is > 0 &&
            command.RoundId is > 0 &&
            command.Generation > 0 &&
            command.DispatchCount > 0;
        error = valid
            ? string.Empty
            : "RecoverRound requires an exact eventId/eventUid/table/device/shoe/round/roundId and a valid command authorization.";
        return valid;
    }

    private static string NormalizeDecision(string decision)
    {
        string normalized = decision.Trim();
        return normalized is
            "RecoverRound" or "AwaitingOperator" or "AlreadyAccepted" or
            "NotRegistered" or "Recovered" or "NotFound" or "Conflict" or "Cancelled" or
            "Expired" or "ManualReview"
                ? normalized
                : string.Empty;
    }

    private static AngelBridgeCommand ToRecoveryCommand(AngelBridgeRecoveryDecision decision) => new()
    {
        CommandId = decision.CommandId,
        Type = "RecoverRound",
        SourceDataCode = decision.SourceDataCode,
        DeviceId = decision.DeviceId,
        EventId = decision.EventId,
        EventUid = decision.EventUid,
        Shoe = decision.Shoe,
        Round = decision.Round,
        RoundId = decision.RoundId,
        Generation = decision.Generation,
        DispatchCount = decision.DispatchCount
    };

    private static BridgePendingEvent ToPendingIdentity(BridgeRecoveryCandidate candidate) => new(
        candidate.EventId,
        candidate.EventType,
        candidate.SourceDataCode,
        candidate.DeviceId,
        candidate.Shoe,
        candidate.Round,
        PayloadJson: string.Empty,
        RetryCount: 0,
        candidate.EventUid);

    private static AngelBridgeUnconfirmedEvent ToUnconfirmedSummary(
        BridgeRecoveryCandidate candidate) => new(
        candidate.EventId,
        candidate.EventUid,
        candidate.EventType,
        candidate.SourceDataCode,
        candidate.DeviceId,
        candidate.Shoe,
        candidate.Round,
        candidate.RoundId,
        candidate.AttemptedAt);

    private string ResolveBridgeId() =>
        string.IsNullOrWhiteSpace(_settings.BridgeId)
            ? Environment.MachineName
            : _settings.BridgeId.Trim();

    private string ResolveBridgeName() =>
        string.IsNullOrWhiteSpace(_settings.BridgeName)
            ? "AngelEyeBridge"
            : _settings.BridgeName.Trim();

    private static bool IsValidRecoveryCommandId(string commandId) =>
        !string.IsNullOrWhiteSpace(commandId) &&
        RecoveryCommandIdPattern.IsMatch(commandId.Trim());

    private static bool IsTerminalDecision(string decision) =>
        decision is "AwaitingOperator" or "AlreadyAccepted" or "NotRegistered" or
            "Recovered" or "NotFound" or "Conflict" or "Cancelled" or "Expired" or
            "ManualReview";

    private static bool IsCommandTerminalDecision(string decision) =>
        decision is "Recovered" or "NotFound" or "Cancelled" or "Expired" or "ManualReview";

    private static bool HasAnyCommandMetadata(AngelBridgeRecoveryDecision decision) =>
        !string.IsNullOrWhiteSpace(decision.CommandId) ||
        decision.Generation != 0 ||
        decision.DispatchCount != 0;

    private static bool HasCompleteCommandMetadata(AngelBridgeRecoveryDecision decision) =>
        IsValidRecoveryCommandId(decision.CommandId) &&
        decision.Generation > 0 &&
        decision.DispatchCount > 0;

    private static bool IsTerminalRecoveryOutcome(string outcome) =>
        outcome is "Recovered" or "AlreadyAccepted" or "NotFound" or "Conflict" or
            "Rejected" or "Cancelled" or "Expired" or "ManualReview";

    private void LogCommandResult(AngelBridgeCommand command, BridgeCommandHandlingResult result)
    {
        if (string.Equals(result.Status, "Deferred", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string status = result.Success ? "完成" : "未完成";
        OnLogReceived?.Invoke(
            $"BMS 命令 {command.Type} commandId={command.CommandId}, eventId={command.EventId}, eventUid={command.EventUid}, generation={command.Generation}, dispatchCount={command.DispatchCount}, sourceDataCode={command.SourceDataCode}, deviceId={command.DeviceId}, shoe={command.Shoe}, round={command.Round} {status}: {result.Message}");
    }

    private Task<BridgeCommandHandlingResult> HandleCommandAsync(AngelBridgeCommand command, CancellationToken cancellationToken)
    {
        if (_commandHandler == null)
        {
            return Task.FromResult(BridgeCommandHandlingResult.Rejected("Bridge command handler is not configured."));
        }

        return _commandHandler(command, cancellationToken);
    }

    private async Task<BridgeSendResult> SendJsonAsync(
        Uri uri,
        string json,
        long expectedEventId,
        string expectedEventUid,
        CancellationToken cancellationToken)
    {
        try
        {
            AuthenticatedPostResponse authenticated = await SendAuthenticatedPostAsync(
                    uri,
                    json,
                    expectedEventUid,
                    cancellationToken)
                .ConfigureAwait(false);
            using HttpResponseMessage response = authenticated.Response;
            string responseText = authenticated.Body;
            if (!response.IsSuccessStatusCode)
            {
                int statusCode = (int)response.StatusCode;
                string error = $"{statusCode} {response.ReasonPhrase} {TrimForLog(responseText)}";
                return IsDefinitiveHttpRejection(statusCode)
                    ? BridgeSendResult.Rejected(statusCode, error)
                    : BridgeSendResult.Unconfirmed(statusCode, error);
            }

            BridgeAckDisposition ack = ClassifyAck(
                responseText,
                expectedEventId,
                expectedEventUid);
            if (ack != BridgeAckDisposition.Accepted)
            {
                return ack == BridgeAckDisposition.Rejected
                    ? BridgeSendResult.Rejected(
                        (int)response.StatusCode,
                        $"ACK rejected {TrimForLog(responseText)}")
                    : BridgeSendResult.Unconfirmed(
                        (int)response.StatusCode,
                        $"ACK missing or invalid {TrimForLog(responseText)}");
            }

            return BridgeSendResult.Ok((int)response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BridgeSendResult.Unconfirmed(
                null,
                BridgeDiagnosticFormatter.FormatException(ex));
        }
    }

    /// <summary>
    /// Stops the dispatcher and releases HTTP and synchronization resources.
    /// </summary>
    public void Dispose()
    {
        if (IsRunning)
        {
            StopAsync().GetAwaiter().GetResult();
        }

        _httpClient.Dispose();
        _sendLock.Dispose();
    }

    internal static Uri NormalizeUrl(string url)
    {
        string trimmed = (url ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("請先設定事件 API 路徑。");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException(
                "BMS API URL 必須是完整的 HTTPS 位址；不允許 HTTP 或省略 scheme。");
        }

        return uri;
    }

    private async Task<AuthenticatedPostResponse> SendAuthenticatedPostAsync(
        Uri uri,
        string json,
        string correlationId,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using HttpRequestMessage request = new(HttpMethod.Post, uri);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation(
                BridgeDiagnosticFormatter.CorrelationHeaderName,
                BridgeDiagnosticFormatter.NormalizeCorrelationId(
                    correlationId,
                    $"angel-request-{Guid.NewGuid():N}"));
            string usedToken = await ApplyBearerTokenAsync(request, cancellationToken)
                .ConfigureAwait(false);

            HttpResponseMessage response = await _httpClient
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            string body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
                _accessTokenProvider != null &&
                attempt == 0)
            {
                response.Dispose();
                _accessTokenProvider.InvalidateAccessToken(usedToken);
                continue;
            }

            return new AuthenticatedPostResponse(response, body);
        }

        throw new InvalidOperationException(
            "BMS authorization retry did not produce a response.");
    }

    private async ValueTask<string> ApplyBearerTokenAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string token;
        if (_accessTokenProvider != null)
        {
            token = await _accessTokenProvider
                .GetAccessTokenAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            token = _bearerTokenProvider?.Invoke() ?? _settings.Token;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "BMS access token provider did not return a token; request was not sent.");
        }

        string normalized = token.Trim();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", normalized);
        return normalized;
    }

    private static void EnsureOnlyOneTokenProvider(
        Func<string>? bearerTokenProvider,
        IBmsAccessTokenProvider? accessTokenProvider)
    {
        if (bearerTokenProvider != null && accessTokenProvider != null)
        {
            throw new InvalidOperationException(
                "Configure either the legacy token callback or the access-token provider, not both.");
        }
    }

    internal static Uri BuildSiblingEndpoint(string url, string lastSegment)
    {
        Uri baseUri = NormalizeUrl(url);
        string path = baseUri.AbsolutePath.TrimEnd('/');
        int lastSlash = path.LastIndexOf('/');
        string newPath = lastSlash >= 0
            ? path[..(lastSlash + 1)] + lastSegment
            : "/" + lastSegment;

        UriBuilder builder = new(baseUri)
        {
            Path = newPath,
            Query = string.Empty
        };
        return builder.Uri;
    }

    private static string BuildEventLabel(BridgePendingEvent pending)
    {
        return $"#{pending.EventId} eventUid={pending.EventUid} {pending.Type} {pending.SourceDataCode} {pending.Shoe}/{pending.Round}";
    }

    private static TimeSpan ResolveNextRecoveryDelay(AngelBridgeHeartbeatResponse? response)
    {
        int seconds = response?.NextPollSeconds ?? (int)DefaultRecoveryPollDelay.TotalSeconds;
        seconds = Math.Clamp(seconds, (int)MinRecoveryPollDelay.TotalSeconds, (int)MaxRecoveryPollDelay.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    private static TimeSpan CalculateRecoveryErrorDelay(int failureCount)
    {
        int seconds = failureCount switch
        {
            <= 1 => 30,
            2 => 60,
            3 => 120,
            _ => (int)MaxRecoveryPollDelay.TotalSeconds
        };
        return TimeSpan.FromSeconds(seconds);
    }

    internal static BridgeAckDisposition ClassifyAck(
        string responseText,
        long expectedEventId,
        string expectedEventUid)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return BridgeAckDisposition.Unconfirmed;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseText);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetPropertyIgnoreCase(root, "errCode", out JsonElement errCode) ||
                !errCode.TryGetInt32(out int code))
            {
                return BridgeAckDisposition.Unconfirmed;
            }

            if (!TryGetPropertyIgnoreCase(root, "data", out JsonElement data) ||
                data.ValueKind != JsonValueKind.Object ||
                !TryGetPropertyIgnoreCase(data, "accepted", out JsonElement accepted) ||
                accepted.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !TryGetPropertyIgnoreCase(data, "duplicate", out JsonElement duplicate) ||
                duplicate.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !TryGetPropertyIgnoreCase(data, "eventId", out JsonElement eventId) ||
                !eventId.TryGetInt64(out long actualEventId) ||
                !TryGetPropertyIgnoreCase(data, "eventUid", out JsonElement eventUid) ||
                eventUid.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(eventUid.GetString(), out Guid actualEventUid) ||
                !Guid.TryParse(expectedEventUid, out Guid expectedUid) ||
                actualEventId != expectedEventId ||
                actualEventUid != expectedUid)
            {
                return BridgeAckDisposition.Unconfirmed;
            }

            if (code != 0)
            {
                return BridgeAckDisposition.Rejected;
            }

            return accepted.GetBoolean() || duplicate.GetBoolean()
                ? BridgeAckDisposition.Accepted
                : BridgeAckDisposition.Rejected;
        }
        catch (JsonException)
        {
            return BridgeAckDisposition.Unconfirmed;
        }
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsDefinitiveHttpRejection(int statusCode) =>
        statusCode is >= 400 and < 500 &&
        statusCode is not 408 and not 425 and not 429;

    private sealed record AuthenticatedPostResponse(
        HttpResponseMessage Response,
        string Body);

    private static string TrimForLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return BridgeDiagnosticFormatter.SanitizeForLog(text, maxLength: 160);
    }
}

/// <summary>
/// Wraps standard BMS API responses.
/// </summary>
/// <typeparam name="T">Response payload type.</typeparam>
public sealed class BmsResponseEnvelope<T>
{
    /// <summary>BMS response error code; zero means success.</summary>
    public int ErrCode { get; init; }

    /// <summary>BMS response error message.</summary>
    public string ErrMsg { get; init; } = string.Empty;

    /// <summary>Response payload.</summary>
    public T? Data { get; init; }
}

/// <summary>
/// Heartbeat payload sent by the bridge to BMS for status reporting and command polling.
/// </summary>
/// <param name="BridgeId">Stable bridge identifier, currently the Windows machine name.</param>
/// <param name="BridgeName">Human-readable bridge name.</param>
/// <param name="Version">Bridge application version.</param>
/// <param name="MachineName">Windows machine name.</param>
/// <param name="SentAt">Heartbeat send time.</param>
/// <param name="Endpoints">Per-shoe endpoint status snapshots.</param>
public sealed record AngelBridgeHeartbeatRequest(
    string BridgeId,
    string BridgeName,
    string Version,
    string MachineName,
    DateTimeOffset SentAt,
    IReadOnlyList<AngelBridgeHeartbeatEndpointStatus> Endpoints,
    string Environment,
    IReadOnlyList<AngelBridgeUnconfirmedEvent> UnconfirmedEvents);

/// <summary>
/// Identity-only summary used to reconcile an ACK-unknown event. It deliberately
/// excludes cards, winner, pair and raw protocol data.
/// </summary>
public sealed record AngelBridgeUnconfirmedEvent(
    long EventId,
    string EventUid,
    string EventType,
    string SourceDataCode,
    string DeviceId,
    long Shoe,
    long Round,
    long? RoundId,
    DateTimeOffset AttemptedAt);

/// <summary>
/// Per-shoe status included in the heartbeat payload.
/// </summary>
public sealed record AngelBridgeHeartbeatEndpointStatus
{
    /// <summary>Display desk name.</summary>
    public string DeskName { get; init; } = string.Empty;

    /// <summary>BMS source table code.</summary>
    public string SourceDataCode { get; init; } = string.Empty;

    /// <summary>BMS SourceData ID used for diagnostics.</summary>
    public string SourceDataId { get; init; } = string.Empty;

    /// <summary>Bridge-side shoe identifier.</summary>
    public string ShoeId { get; init; } = string.Empty;

    /// <summary>Bridge-side physical or mock device identifier.</summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>Configured physical connection display value.</summary>
    public string ComPort { get; init; } = string.Empty;

    /// <summary>Configured endpoint connection mode.</summary>
    public string ConnectionMode { get; init; } = string.Empty;

    /// <summary>MOXA/NPort host when using direct TCP mode.</summary>
    public string MoxaHost { get; init; } = string.Empty;

    /// <summary>MOXA/NPort TCP server port when using direct TCP mode.</summary>
    public int? MoxaPort { get; init; }

    /// <summary>Whether this endpoint is enabled locally.</summary>
    public bool Enabled { get; init; }

    /// <summary>Whether events from this endpoint are currently transmitted to BMS.</summary>
    public bool BmsTransmitEnabled { get; init; }

    /// <summary>Whether this endpoint runs in mock mode.</summary>
    public bool MockMode { get; init; }

    /// <summary>Current local connection status.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Current BMS shoe number.</summary>
    public long Shoe { get; init; }

    /// <summary>Current BMS round number.</summary>
    public long Round { get; init; }

    /// <summary>Current bridge round identifier.</summary>
    public long? RoundId { get; init; }

    /// <summary>Pending local outbox event count.</summary>
    public int PendingOutboxCount { get; init; }

    /// <summary>Failed local outbox event count.</summary>
    public int FailedOutboxCount { get; init; }

    /// <summary>Last visible endpoint event text.</summary>
    public string LastEvent { get; init; } = string.Empty;
}

/// <summary>
/// Heartbeat response returned by BMS.
/// </summary>
public sealed record AngelBridgeHeartbeatResponse
{
    /// <summary>Whether BMS accepted the heartbeat.</summary>
    public bool Accepted { get; init; }

    /// <summary>BMS response message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>BMS server time.</summary>
    public DateTimeOffset ServerTime { get; init; }

    /// <summary>Commands BMS wants the bridge to handle after the heartbeat.</summary>
    public IReadOnlyList<AngelBridgeCommand> Commands { get; init; } = [];

    /// <summary>Explicit decisions for submitted delivery-unknown identities.</summary>
    public IReadOnlyList<AngelBridgeRecoveryDecision> Decisions { get; init; } = [];

    /// <summary>Recommended seconds before the next recovery poll.</summary>
    public int NextPollSeconds { get; init; } = 15;

    /// <summary>Whether BMS intentionally throttled this polling request.</summary>
    public bool RateLimited { get; init; }
}

/// <summary>
/// Command returned by BMS in a heartbeat response.
/// </summary>
public sealed record AngelBridgeCommand
{
    /// <summary>BMS command identifier used for logs and deduplication.</summary>
    public string CommandId { get; init; } = string.Empty;

    /// <summary>Command type, for example RecoverRound or ResendEvent.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>BMS source table code for the target shoe endpoint.</summary>
    public string SourceDataCode { get; init; } = string.Empty;

    /// <summary>Bridge-side shoe device identifier.</summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>Bridge local event ID for ResendEvent commands.</summary>
    public long? EventId { get; init; }

    /// <summary>Stable event identity that must match the retained payload.</summary>
    public string EventUid { get; init; } = string.Empty;

    /// <summary>Optional event type filter for ResendEvent commands.</summary>
    public string EventType { get; init; } = string.Empty;

    /// <summary>BMS shoe number for recovery or resend lookup.</summary>
    public long? Shoe { get; init; }

    /// <summary>BMS round number for recovery or resend lookup.</summary>
    public long? Round { get; init; }

    /// <summary>Bridge round identifier for recovery or resend lookup.</summary>
    public long? RoundId { get; init; }

    /// <summary>Operator investigation generation for the logical command.</summary>
    public int Generation { get; init; }

    /// <summary>Monotonic authoritative BMS lease/reissue count.</summary>
    public int DispatchCount { get; init; }
}

/// <summary>One explicit reconciliation decision returned for an event summary.</summary>
public sealed record AngelBridgeRecoveryDecision
{
    public long? EventId { get; init; }

    public string EventUid { get; init; } = string.Empty;

    public string Decision { get; init; } = string.Empty;

    public string CommandId { get; init; } = string.Empty;

    public string SourceDataCode { get; init; } = string.Empty;

    public string DeviceId { get; init; } = string.Empty;

    public long? Shoe { get; init; }

    public long? Round { get; init; }

    public long? RoundId { get; init; }

    public int Generation { get; init; }

    public int DispatchCount { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>Command-authorized payload sent only to the historical recovery endpoint.</summary>
public sealed record AngelBridgeRecoverySubmission
{
    public string CommandId { get; init; } = string.Empty;

    public int Generation { get; init; }

    public int DispatchCount { get; init; }

    public string BridgeId { get; init; } = string.Empty;

    public string SourceDataCode { get; init; } = string.Empty;

    public string DeviceId { get; init; } = string.Empty;

    public long Shoe { get; init; }

    public long Round { get; init; }

    public long? RoundId { get; init; }

    public long EventId { get; init; }

    public string EventUid { get; init; } = string.Empty;

    public string Outcome { get; init; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? GameResult { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }
}

/// <summary>Strict acknowledgement for one dedicated recovery submission.</summary>
public sealed record AngelBridgeRecoveryAcknowledgement
{
    public bool Accepted { get; init; }

    public string CommandId { get; init; } = string.Empty;

    public int Generation { get; init; }

    public int DispatchCount { get; init; }

    public string EventUid { get; init; } = string.Empty;

    public string Outcome { get; init; } = string.Empty;

    public bool Duplicate { get; init; }

    public string State { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Result of handling a BMS command returned by heartbeat polling.
/// </summary>
/// <param name="Success">Whether the command was accepted and handled locally.</param>
/// <param name="Status">Machine-readable result status.</param>
/// <param name="Message">Human-readable result detail.</param>
public sealed record BridgeCommandHandlingResult(bool Success, string Status, string Message)
{
    /// <summary>Creates a handled result.</summary>
    /// <param name="message">Human-readable result detail.</param>
    /// <returns>A successful command result.</returns>
    public static BridgeCommandHandlingResult Handled(string message) => new(true, "Handled", message);

    /// <summary>Creates a not-found result.</summary>
    /// <param name="message">Human-readable result detail.</param>
    /// <returns>A not-found command result.</returns>
    public static BridgeCommandHandlingResult NotFound(string message) => new(false, "NotFound", message);

    /// <summary>Creates a deferred result for commands that are intentionally waiting for their next local retry window.</summary>
    /// <param name="message">Human-readable result detail.</param>
    /// <returns>A deferred command result.</returns>
    public static BridgeCommandHandlingResult Deferred(string message) => new(false, "Deferred", message);

    /// <summary>Creates a rejected result.</summary>
    /// <param name="message">Human-readable result detail.</param>
    /// <returns>A rejected command result.</returns>
    public static BridgeCommandHandlingResult Rejected(string message) => new(false, "Rejected", message);
}

/// <summary>
/// Configures the BMS event API endpoint and bearer token used by the bridge.
/// </summary>
/// <param name="Url">Target BMS event API URL.</param>
/// <param name="Token">Bearer token sent with each request.</param>
public sealed record BmsApiSettings(
    string Url,
    string Token,
    string BridgeId = "",
    string BridgeName = "",
    string Environment = "")
{
    /// <summary>An empty API configuration.</summary>
    public static BmsApiSettings Empty { get; } = new(string.Empty, string.Empty);
}

/// <summary>
/// Supplies a short-lived BMS access token at request time. Implementations own the
/// client-credentials exchange, expiry cache and refresh-before-expiry policy.
/// </summary>
public interface IBmsAccessTokenProvider
{
    /// <summary>Returns a currently usable access token without exposing client credentials.</summary>
    ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Invalidates a token rejected with HTTP 401 so one request may obtain a fresh
    /// token and retry before any event is ingested.
    /// </summary>
    void InvalidateAccessToken(string rejectedAccessToken);
}

/// <summary>
/// Exchanges per-bridge client credentials for a short-lived ANGEL access token
/// and refreshes it shortly before expiry.
/// </summary>
public sealed class BmsClientCredentialsAccessTokenProvider : IBmsAccessTokenProvider, IDisposable
{
    private static readonly JsonSerializerOptions TokenJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly Uri _tokenEndpoint;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _expectedBridgeId;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string _accessToken = string.Empty;
    private DateTimeOffset _refreshAt = DateTimeOffset.MinValue;
    private bool _disposed;

    /// <summary>Creates a provider using the default HTTPS transport.</summary>
    public BmsClientCredentialsAccessTokenProvider(
        string eventApiUrl,
        string clientId,
        string clientSecret,
        string expectedBridgeId)
        : this(
            eventApiUrl,
            clientId,
            clientSecret,
            expectedBridgeId,
            BmsApiClient.CreateSecureHttpHandler(),
            static () => DateTimeOffset.UtcNow)
    {
    }

    internal BmsClientCredentialsAccessTokenProvider(
        string eventApiUrl,
        string clientId,
        string clientSecret,
        string expectedBridgeId,
        HttpMessageHandler handler,
        Func<DateTimeOffset> utcNow)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(utcNow);

        _tokenEndpoint = BmsApiClient.BuildSiblingEndpoint(eventApiUrl, "token");
        _clientId = RequireCredential(clientId, "clientId");
        _clientSecret = RequireCredential(clientSecret, "clientSecret");
        _expectedBridgeId = RequireCredential(expectedBridgeId, "expectedBridgeId");
        _utcNow = utcNow;
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
    }

    /// <inheritdoc />
    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        DateTimeOffset now = _utcNow();
        if (_accessToken.Length > 0 && now < _refreshAt)
        {
            return _accessToken;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _utcNow();
            if (_accessToken.Length > 0 && now < _refreshAt)
            {
                return _accessToken;
            }

            using HttpRequestMessage request = new(HttpMethod.Post, _tokenEndpoint);
            request.Headers.TryAddWithoutValidation(
                BridgeDiagnosticFormatter.CorrelationHeaderName,
                $"angel-token-{Guid.NewGuid():N}");
            string json = JsonSerializer.Serialize(
                new BmsClientCredentialsTokenRequest(_clientId, _clientSecret),
                TokenJsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            string responseText = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"BMS token endpoint rejected client credentials with HTTP {(int)response.StatusCode}.");
            }

            BmsClientCredentialsTokenEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<
                    BmsClientCredentialsTokenEnvelope>(
                    responseText,
                    TokenJsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "BMS token endpoint returned an invalid JSON envelope.",
                    ex);
            }

            BmsClientCredentialsTokenResponse? data = envelope?.Data;
            if (envelope?.ErrCode != 0 ||
                data == null ||
                string.IsNullOrWhiteSpace(data.AccessToken) ||
                !string.Equals(data.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase) ||
                data.ExpiresInSeconds <= 0 ||
                data.ExpiresAt <= now ||
                !string.Equals(data.BridgeId, _expectedBridgeId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "BMS token response was incomplete or did not match the expected bridge identity.");
            }

            DateTimeOffset relativeExpiry = now.AddSeconds(data.ExpiresInSeconds);
            DateTimeOffset effectiveExpiry =
                data.ExpiresAt < relativeExpiry ? data.ExpiresAt : relativeExpiry;
            TimeSpan remaining = effectiveExpiry - now;
            if (remaining <= TimeSpan.Zero)
            {
                throw new InvalidOperationException("BMS returned an already-expired access token.");
            }

            double refreshSkewSeconds = Math.Min(
                60,
                Math.Max(1, remaining.TotalSeconds * 0.1));
            if (refreshSkewSeconds >= remaining.TotalSeconds)
            {
                refreshSkewSeconds = remaining.TotalSeconds / 2;
            }

            _accessToken = data.AccessToken.Trim();
            _refreshAt = effectiveExpiry.Subtract(TimeSpan.FromSeconds(refreshSkewSeconds));
            return _accessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <inheritdoc />
    public void InvalidateAccessToken(string rejectedAccessToken)
    {
        string rejected = rejectedAccessToken?.Trim() ?? string.Empty;
        if (rejected.Length == 0)
        {
            return;
        }

        if (string.Equals(_accessToken, rejected, StringComparison.Ordinal))
        {
            _accessToken = string.Empty;
            _refreshAt = DateTimeOffset.MinValue;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
        _refreshLock.Dispose();
    }

    private static string RequireCredential(string value, string name)
    {
        string trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException($"{name} cannot be empty.", name);
        }

        return trimmed;
    }

    private sealed record BmsClientCredentialsTokenRequest(
        string ClientId,
        string ClientSecret);

    private sealed record BmsClientCredentialsTokenResponse
    {
        public string AccessToken { get; init; } = string.Empty;

        public string TokenType { get; init; } = string.Empty;

        public int ExpiresInSeconds { get; init; }

        public DateTimeOffset ExpiresAt { get; init; }

        public string BridgeId { get; init; } = string.Empty;
    }

    private sealed record BmsClientCredentialsTokenEnvelope
    {
        public int? ErrCode { get; init; }

        public BmsClientCredentialsTokenResponse? Data { get; init; }
    }
}

/// <summary>
/// Represents the result of a single event API POST attempt.
/// </summary>
/// <param name="Success">True when BMS accepted the event.</param>
/// <param name="DefinitivelyRejected">True when BMS explicitly rejected the event.</param>
/// <param name="StatusCode">HTTP status code if a response was received.</param>
/// <param name="Error">Failure detail used for retry logging.</param>
public sealed record BridgeSendResult(
    bool Success,
    bool DefinitivelyRejected,
    int? StatusCode,
    string Error)
{
    /// <summary>
    /// Creates a successful send result.
    /// </summary>
    /// <param name="statusCode">HTTP status code from BMS.</param>
    /// <returns>A successful send result.</returns>
    public static BridgeSendResult Ok(int statusCode) =>
        new(true, false, statusCode, string.Empty);

    /// <summary>
    /// Creates an ACK-unknown send result.
    /// </summary>
    /// <param name="statusCode">HTTP status code, or null when no response was received.</param>
    /// <param name="error">Failure detail.</param>
    /// <returns>An ACK-unknown send result.</returns>
    public static BridgeSendResult Unconfirmed(int? statusCode, string error) =>
        new(false, false, statusCode, error);

    /// <summary>Creates a definitive rejection result.</summary>
    public static BridgeSendResult Rejected(int? statusCode, string error) =>
        new(false, true, statusCode, error);
}

internal sealed record RecoveryPostResult(
    bool Success,
    string Outcome,
    string Message)
{
    public static RecoveryPostResult Succeeded(string outcome, string message) =>
        new(true, outcome, message);

    public static RecoveryPostResult Failed(string outcome, string message) =>
        new(false, outcome, message);
}

/// <summary>Classification of a BMS event acknowledgement body.</summary>
internal enum BridgeAckDisposition
{
    Accepted,
    Rejected,
    Unconfirmed
}

/// <summary>
/// Verifies that the durable event identity and the exact HTTP payload cannot diverge.
/// </summary>
public static class BridgeEventUidValidator
{
    /// <summary>
    /// Validates the SQLite event_uid column against the eventUid in the stored JSON payload.
    /// </summary>
    public static bool TryValidate(BridgePendingEvent pending, out string error)
    {
        if (!Guid.TryParse(pending.EventUid, out Guid storedEventUid) ||
            storedEventUid == Guid.Empty)
        {
            error = "SQLite event_uid is missing or invalid";
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(pending.PayloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("eventUid", out JsonElement eventUidElement) ||
                eventUidElement.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(eventUidElement.GetString(), out Guid payloadEventUid) ||
                payloadEventUid == Guid.Empty)
            {
                error = "stored payload eventUid is missing or invalid";
                return false;
            }

            if (storedEventUid != payloadEventUid)
            {
                error = "SQLite event_uid does not match stored payload eventUid";
                return false;
            }
        }
        catch (JsonException)
        {
            error = "stored payload is not valid JSON";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
