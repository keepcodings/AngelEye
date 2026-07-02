using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;

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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private BmsApiSettings _settings = BmsApiSettings.Empty;
    private BridgeEventJournal? _journal;
    private Func<BridgePendingEvent, bool> _canDispatchEvent = _ => true;
    private Func<IReadOnlyList<AngelBridgeHeartbeatEndpointStatus>>? _heartbeatSnapshotProvider;
    private Func<AngelBridgeCommand, CancellationToken, Task<BridgeCommandHandlingResult>>? _commandHandler;
    private CancellationTokenSource? _dispatcherCancellation;
    private Task? _dispatcherTask;
    private Task? _heartbeatTask;
    private int _recoveryFailureCount;

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
    public void Start(
        BmsApiSettings settings,
        BridgeEventJournal journal,
        Func<BridgePendingEvent, bool>? canDispatchEvent = null,
        Func<IReadOnlyList<AngelBridgeHeartbeatEndpointStatus>>? heartbeatSnapshotProvider = null,
        Func<AngelBridgeCommand, CancellationToken, Task<BridgeCommandHandlingResult>>? commandHandler = null)
    {
        Uri uri = NormalizeUrl(settings.Url);
        _settings = settings with { Url = uri.ToString() };
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _canDispatchEvent = canDispatchEvent ?? (_ => true);
        _heartbeatSnapshotProvider = heartbeatSnapshotProvider;
        _commandHandler = commandHandler;
        IsRunning = true;
        OnStatusChanged?.Invoke("傳送中");
        OnLogReceived?.Invoke($"事件 API 傳送已開始: {uri}，SQLite Outbox 已接管送出。");

        _dispatcherCancellation = new CancellationTokenSource();
        _dispatcherTask = Task.Run(() => DispatchLoopAsync(_dispatcherCancellation.Token));
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_dispatcherCancellation.Token));
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
                OnLogReceived?.Invoke($"Outbox dispatcher error: {ex.Message}");
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
            OnLogReceived?.Invoke($"事件 API URL 無效: {ex.Message}");
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

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                BridgeSendResult result = await SendJsonAsync(uri, pending.PayloadJson, cancellationToken).ConfigureAwait(false);
                DateTime now = DateTime.UtcNow;
                string eventLabel = BuildEventLabel(pending);
                if (result.Success)
                {
                    await journal.MarkSentAsync(pending.EventId, now).ConfigureAwait(false);
                    OnLogReceived?.Invoke($"POST {eventLabel} -> {result.StatusCode}，已標記送達。");
                }
                else
                {
                    int retryCount = pending.RetryCount + 1;
                    await journal.MarkFailedAsync(pending.EventId, retryCount, now, result.Error).ConfigureAwait(false);
                    OnLogReceived?.Invoke($"POST {eventLabel} 失敗: {result.Error}，稍後重試 #{retryCount}。");
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
                OnLogReceived?.Invoke($"BMS 補償查詢 error: {ex.Message}，{delay.TotalSeconds:0} 秒後重試。");
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
            OnLogReceived?.Invoke($"BMS 補償查詢 URL 無效: {ex.Message}");
            return DefaultRecoveryPollDelay;
        }

        AngelBridgeHeartbeatRequest heartbeat = new(
            BridgeId: Environment.MachineName,
            BridgeName: "XtasysBridge",
            Version: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0",
            MachineName: Environment.MachineName,
            SentAt: DateTimeOffset.UtcNow,
            Endpoints: _heartbeatSnapshotProvider());

        string json = JsonSerializer.Serialize(heartbeat, JsonOptions);
        using HttpRequestMessage request = new(HttpMethod.Post, recoveryUri);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (!string.IsNullOrWhiteSpace(_settings.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token);
        }

        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _recoveryFailureCount++;
            TimeSpan retryDelay = CalculateRecoveryErrorDelay(_recoveryFailureCount);
            OnLogReceived?.Invoke($"BMS 補償查詢 -> {(int)response.StatusCode} {response.ReasonPhrase}，{retryDelay.TotalSeconds:0} 秒後重試。");
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
            OnLogReceived?.Invoke($"BMS 補償查詢 ACK JSON 無法解析: {ex.Message}，{retryDelay.TotalSeconds:0} 秒後重試。");
            return retryDelay;
        }

        if (envelope == null || envelope.ErrCode != 0 || envelope.Data?.Accepted == false)
        {
            _recoveryFailureCount++;
            TimeSpan retryDelay = CalculateRecoveryErrorDelay(_recoveryFailureCount);
            OnLogReceived?.Invoke($"BMS 補償查詢 ACK rejected {TrimForLog(responseText)}，{retryDelay.TotalSeconds:0} 秒後重試。");
            return retryDelay;
        }

        _recoveryFailureCount = 0;
        TimeSpan nextDelay = ResolveNextRecoveryDelay(envelope.Data);
        if (envelope.Data?.RateLimited == true)
        {
            OnLogReceived?.Invoke($"BMS 補償查詢已節流，{nextDelay.TotalSeconds:0} 秒後再查。");
        }

        IReadOnlyList<AngelBridgeCommand> commands = envelope.Data?.Commands ?? [];
        if (commands.Count == 0)
        {
            return nextDelay;
        }

        OnLogReceived?.Invoke($"BMS 補償查詢收到 {commands.Count} 筆補送命令。");
        foreach (AngelBridgeCommand command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BridgeCommandHandlingResult result = await HandleCommandAsync(command, cancellationToken).ConfigureAwait(false);
            string status = result.Success ? "完成" : "未完成";
            OnLogReceived?.Invoke($"BMS 命令 {command.Type}({command.CommandId}) {status}: {result.Message}");
        }

        return nextDelay;
    }

    private Task<BridgeCommandHandlingResult> HandleCommandAsync(AngelBridgeCommand command, CancellationToken cancellationToken)
    {
        if (_commandHandler == null)
        {
            return Task.FromResult(BridgeCommandHandlingResult.Rejected("Bridge command handler is not configured."));
        }

        return _commandHandler(command, cancellationToken);
    }

    private async Task<BridgeSendResult> SendJsonAsync(Uri uri, string json, CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, uri);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(_settings.Token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token);
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return BridgeSendResult.Failed((int)response.StatusCode, $"{(int)response.StatusCode} {response.ReasonPhrase} {TrimForLog(responseText)}");
            }

            if (!IsPositiveAck(responseText))
            {
                return BridgeSendResult.Failed((int)response.StatusCode, $"ACK rejected {TrimForLog(responseText)}");
            }

            return BridgeSendResult.Ok((int)response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return BridgeSendResult.Failed(null, ex.Message);
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

    private static Uri NormalizeUrl(string url)
    {
        string trimmed = (url ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("請先設定事件 API 路徑。");
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = $"http://{trimmed}";
        }

        return new Uri(trimmed, UriKind.Absolute);
    }

    private static Uri BuildSiblingEndpoint(string url, string lastSegment)
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
        return $"#{pending.EventId} {pending.Type} {pending.SourceDataCode} {pending.Shoe}/{pending.Round}";
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

    private static bool IsPositiveAck(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return true;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseText);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("errCode", out JsonElement errCode) && errCode.TryGetInt32(out int code) && code != 0)
            {
                return false;
            }

            if (root.TryGetProperty("data", out JsonElement data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("accepted", out JsonElement accepted)
                && accepted.ValueKind == JsonValueKind.False)
            {
                return false;
            }
        }
        catch (JsonException)
        {
            return true;
        }

        return true;
    }

    private static string TrimForLog(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = text.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 160 ? normalized : normalized[..160] + "...";
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
    IReadOnlyList<AngelBridgeHeartbeatEndpointStatus> Endpoints);

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

    /// <summary>Optional event type filter for ResendEvent commands.</summary>
    public string EventType { get; init; } = string.Empty;

    /// <summary>BMS shoe number for recovery or resend lookup.</summary>
    public long? Shoe { get; init; }

    /// <summary>BMS round number for recovery or resend lookup.</summary>
    public long? Round { get; init; }

    /// <summary>Bridge round identifier for recovery or resend lookup.</summary>
    public long? RoundId { get; init; }
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
public sealed record BmsApiSettings(string Url, string Token)
{
    /// <summary>An empty API configuration.</summary>
    public static BmsApiSettings Empty { get; } = new(string.Empty, string.Empty);
}

/// <summary>
/// Represents the result of a single event API POST attempt.
/// </summary>
/// <param name="Success">True when BMS accepted the event.</param>
/// <param name="StatusCode">HTTP status code if a response was received.</param>
/// <param name="Error">Failure detail used for retry logging.</param>
public sealed record BridgeSendResult(bool Success, int? StatusCode, string Error)
{
    /// <summary>
    /// Creates a successful send result.
    /// </summary>
    /// <param name="statusCode">HTTP status code from BMS.</param>
    /// <returns>A successful send result.</returns>
    public static BridgeSendResult Ok(int statusCode) => new(true, statusCode, string.Empty);

    /// <summary>
    /// Creates a failed send result.
    /// </summary>
    /// <param name="statusCode">HTTP status code, or null when no response was received.</param>
    /// <param name="error">Failure detail.</param>
    /// <returns>A failed send result.</returns>
    public static BridgeSendResult Failed(int? statusCode, string error) => new(false, statusCode, error);
}
