using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AngelEyeBmsBridge;

/// <summary>GET-only client for the Worker TeleBet query API.</summary>
public sealed class TeleBetQueryClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public TeleBetQueryClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _ownsClient = httpClient == null;
    }

    public Task<QueryApiEnvelope<QueryStatusData>> GetStatusAsync(Uri baseUri, CancellationToken cancellationToken = default) =>
        GetAsync<QueryStatusData>(baseUri, "/api/v1/status", cancellationToken);

    public Task<QueryApiEnvelope<List<QueryRoundData>>> GetRoundsAsync(
        Uri baseUri,
        QueryRoundFilters filters,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, string?> values = new()
        {
            ["deskCode"] = filters.DeskCode,
            ["fromUtc"] = filters.FromUtc?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            ["toUtc"] = filters.ToUtc?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            ["shoe"] = filters.Shoe?.ToString(CultureInfo.InvariantCulture),
            ["round"] = filters.Round?.ToString(CultureInfo.InvariantCulture),
            ["state"] = filters.State,
            ["pageSize"] = filters.PageSize.ToString(CultureInfo.InvariantCulture),
            ["cursor"] = cursor
        };
        return GetAsync<List<QueryRoundData>>(baseUri, BuildPath("/api/v1/rounds", values), cancellationToken);
    }

    public Task<QueryApiEnvelope<QueryRoundDetailData>> GetRoundDetailAsync(
        Uri baseUri,
        string deskCode,
        long shoe,
        long round,
        CancellationToken cancellationToken = default) =>
        GetAsync<QueryRoundDetailData>(
            baseUri,
            $"/api/v1/rounds/{Uri.EscapeDataString(deskCode)}/{shoe.ToString(CultureInfo.InvariantCulture)}/{round.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);

    public Task<QueryApiEnvelope<List<QueryEventData>>> GetEventsAsync(
        Uri baseUri,
        QueryEventFilters filters,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        GetAsync<List<QueryEventData>>(baseUri, BuildEventPath("/api/v1/events", filters, cursor, includePayload: filters.IncludePayload), cancellationToken);

    public Task<QueryApiEnvelope<List<QueryEventData>>> GetOutboxAsync(
        Uri baseUri,
        QueryEventFilters filters,
        string? cursor = null,
        CancellationToken cancellationToken = default) =>
        GetAsync<List<QueryEventData>>(baseUri, BuildEventPath("/api/v1/outbox", filters, cursor, includePayload: false), cancellationToken);

    public Task<QueryApiEnvelope<List<QueryRecoveryData>>> GetRecoveriesAsync(
        Uri baseUri,
        string deskCode = "",
        string result = "",
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, string?> values = new()
        {
            ["deskCode"] = deskCode,
            ["result"] = result,
            ["pageSize"] = "100",
            ["cursor"] = cursor
        };
        return GetAsync<List<QueryRecoveryData>>(baseUri, BuildPath("/api/v1/recoveries", values), cancellationToken);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<QueryApiEnvelope<T>> GetAsync<T>(Uri baseUri, string path, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(baseUri, path));
        using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            QueryApiErrorEnvelope? error = TryDeserialize<QueryApiErrorEnvelope>(json);
            throw new TeleBetQueryException(
                (int)response.StatusCode,
                error?.Error.Code ?? "query_failed",
                error?.Error.Message ?? $"Query failed with HTTP {(int)response.StatusCode}.");
        }

        QueryApiEnvelope<T>? envelope = TryDeserialize<QueryApiEnvelope<T>>(json);
        return envelope ?? throw new TeleBetQueryException(0, "invalid_response", "Worker returned an invalid query response.");
    }

    private static T? TryDeserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, QueryJson.Options);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string BuildEventPath(string route, QueryEventFilters filters, string? cursor, bool includePayload)
    {
        Dictionary<string, string?> values = new()
        {
            ["deskCode"] = filters.DeskCode,
            ["type"] = filters.Type,
            ["status"] = filters.Status,
            ["fromUtc"] = filters.FromUtc?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            ["toUtc"] = filters.ToUtc?.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
            ["shoe"] = filters.Shoe?.ToString(CultureInfo.InvariantCulture),
            ["round"] = filters.Round?.ToString(CultureInfo.InvariantCulture),
            ["pageSize"] = filters.PageSize.ToString(CultureInfo.InvariantCulture),
            ["includePayload"] = includePayload ? "true" : null,
            ["cursor"] = cursor
        };
        return BuildPath(route, values);
    }

    private static string BuildPath(string route, IReadOnlyDictionary<string, string?> values)
    {
        string query = string.Join("&", values
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}"));
        return string.IsNullOrEmpty(query) ? route : $"{route}?{query}";
    }
}

public sealed class QueryConsoleState
{
    public QueryStatusData? LastStatus { get; private set; }
    public DateTimeOffset? LastSuccessfulUtc { get; private set; }
    public bool IsOffline { get; private set; }
    public string LastError { get; private set; } = string.Empty;

    public void MarkSuccess(QueryStatusData status, DateTimeOffset now)
    {
        LastStatus = status;
        LastSuccessfulUtc = now;
        IsOffline = false;
        LastError = string.Empty;
    }

    public void MarkFailure(string error)
    {
        IsOffline = true;
        LastError = error;
    }
}

public sealed class TeleBetQueryException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public static class QueryDisplayText
{
    public static string Delivery(string? status) => string.Equals(status, "Sent", StringComparison.OrdinalIgnoreCase)
        ? "BMS endpoint accepted"
        : string.IsNullOrWhiteSpace(status) ? "尚無傳送證據" : status;

    public static string RoundState(QueryRoundData round) => round.IsComplete
        ? "已結算"
        : $"{round.State}（本機無 GameResult）";
}

/// <summary>Pure presentation rules shared by the Query Console and its regression tests.</summary>
public static class QueryConsoleProjection
{
    public static bool IsMetadataVerified(QueryMetadata metadata, QueryServerProfile profile) =>
        !string.IsNullOrWhiteSpace(metadata.InstanceName) &&
        string.Equals(metadata.Environment, profile.ExpectedEnvironment, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(metadata.Role, profile.ExpectedRole, StringComparison.OrdinalIgnoreCase);

    public static string ConnectionState(QueryEndpointData endpoint) => endpoint.Connected
        ? "正常"
        : endpoint.Enabled ? "中斷" : "停用";

    public static IReadOnlyList<QueryRoundData> MergeRounds(
        IEnumerable<QueryRoundData> existing,
        IEnumerable<QueryRoundData> incoming)
    {
        List<QueryRoundData> merged = [];
        HashSet<(string Desk, long Shoe, long Round)> seen = [];
        foreach (QueryRoundData round in existing.Concat(incoming))
        {
            if (seen.Add((round.SourceDataCode, round.Shoe, round.Round)))
            {
                merged.Add(round);
            }
        }
        return merged;
    }

    public static IReadOnlyList<QueryExceptionItem> BuildExceptions(
        IEnumerable<QueryRoundData> rounds,
        IEnumerable<QueryEventData> failed,
        IEnumerable<QueryEventData> pending,
        IEnumerable<QueryRecoveryData> recoveries,
        DateTimeOffset now)
    {
        List<QueryExceptionItem> rows = [];
        rows.AddRange(rounds.Where(round => !round.IsComplete).Select(round => new QueryExceptionItem(
            "警告", "缺 GameResult", round.SourceDataCode, $"{round.Shoe}/{round.Round}", round.UpdatedUtc,
            "Worker 本機有開局／牌訊，但沒有 GameResult；請比對現場 G frame 與 BMS 紀錄。", null)));
        rows.AddRange(failed.Select(item => new QueryExceptionItem(
            "錯誤", "Outbox 失敗", item.SourceDataCode, $"{item.Shoe}/{item.Round}", item.LastAttemptUtc ?? item.OccurredUtc,
            $"Event #{item.EventId}：{item.LastError}", item.NextRetryUtc)));
        DateTimeOffset pendingThreshold = now.AddMinutes(-1);
        rows.AddRange(pending.Where(item => ParseUtc(item.OccurredUtc) < pendingThreshold).Select(item => new QueryExceptionItem(
            "警告", "Outbox 逾時待送", item.SourceDataCode, $"{item.Shoe}/{item.Round}", item.OccurredUtc,
            $"Event #{item.EventId} 已待送超過 1 分鐘。", item.NextRetryUtc)));
        rows.AddRange(recoveries.Where(item => item.Result is "NotFound" or "Backoff").Select(item => new QueryExceptionItem(
            "警告", $"RecoverRound {item.Result}", item.SourceDataCode, $"{item.Shoe}/{item.Round}", item.LastObservedUtc,
            $"{item.CommandId}：{item.Message}", item.NextRetryUtc)));
        return rows.OrderByDescending(row => ParseUtc(row.Time)).ToList();
    }

    private static DateTimeOffset ParseUtc(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? parsed.ToUniversalTime()
            : DateTimeOffset.MinValue;
}

internal static class QueryJson
{
    internal static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

public sealed record QueryServerProfile(string Name, Uri BaseUri, string ExpectedEnvironment, string ExpectedRole)
{
    public static IReadOnlyList<QueryServerProfile> Defaults { get; } =
    [
        new("QA .29", new Uri("http://127.0.0.1:18080"), "QA", "Primary"),
        new("正式主用 .30", new Uri("http://127.0.0.1:18081"), "Production", "Primary"),
        new("正式備援 .31", new Uri("http://127.0.0.1:18082"), "Production", "Standby")
    ];
}

public sealed record QueryApiEnvelope<T>(QueryMetadata Meta, T Data, string? NextCursor);
public sealed record QueryMetadata(string ApiVersion, string InstanceName, string Environment, string Role, DateTimeOffset GeneratedAtUtc);
public sealed record QueryApiErrorEnvelope(QueryApiError Error);
public sealed record QueryApiError(string Code, string Message);
public sealed record QueryStatusData(string BridgeId, string BridgeName, string Version, bool BmsDispatcherRunning, List<QueryEndpointData> Endpoints);
public sealed record QueryEndpointData(string DeskName, string SourceDataCode, string DeviceId, bool Enabled, bool Connected, string Status, DateTimeOffset? LastEventUtc, string LastEvent, long Shoe, long Round, long? RoundId, bool BmsTransmitEnabled, int PendingCount, int FailedCount);
public sealed record QueryRoundFilters(string DeskCode, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, long? Shoe, long? Round, string State, int PageSize = 50);
public sealed record QueryRoundData(string SourceDataCode, string DeviceId, long Shoe, long Round, long? RoundId, string? StartedUtc, string? SettledUtc, string State, string? CardsJson, string? ResultJson, string UpdatedUtc, bool IsComplete, string? DeliveryStatus, int RetryCount, string? LastAttemptUtc, string? SentUtc, string? LastError, int? HttpStatus);
public sealed record QueryRoundDetailData(QueryRoundData Round, List<QueryEventData> Timeline);
public sealed record QueryEventFilters(string DeskCode = "", string Type = "", string Status = "", DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null, long? Shoe = null, long? Round = null, int PageSize = 100, bool IncludePayload = false);
public sealed record QueryEventData(long EventId, string OccurredUtc, string Type, string SourceDataCode, string DeviceId, long Shoe, long Round, long? RoundId, string Status, int RetryCount, string? NextRetryUtc, string? LastAttemptUtc, string? SentUtc, string? LastError, int? HttpStatus, string? PayloadJson);
public sealed record QueryRecoveryData(string CommandId, string CommandType, string SourceDataCode, string DeviceId, long? Shoe, long? Round, long? RoundId, string ReceivedUtc, string LastObservedUtc, string Result, string? NextRetryUtc, string? Message);
public sealed record QueryExceptionItem(string Level, string Type, string Desk, string Round, string Time, string Message, string? NextRetry);
