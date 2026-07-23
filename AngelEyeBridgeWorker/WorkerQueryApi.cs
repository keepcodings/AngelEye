using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Text;

namespace AngelEyeBmsBridge;

/// <summary>Routes the Worker's loopback health and versioned read-only query requests.</summary>
public sealed class WorkerHttpRouter
{
    private static readonly JsonSerializerOptions QueryJsonOptions = new(WorkerSettings.JsonOptions)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly Func<string, (int Status, string Body)> _legacyHealth;
    private readonly Func<WorkerStatusData> _statusProvider;
    private readonly WorkerQuerySource _source;
    private readonly BridgeEventJournal? _journal;
    private readonly Action<string>? _logError;

    public WorkerHttpRouter(
        WorkerQuerySource source,
        Func<string, (int Status, string Body)> legacyHealth,
        Func<WorkerStatusData> statusProvider,
        BridgeEventJournal? journal = null,
        Action<string>? logError = null)
    {
        _source = source;
        _legacyHealth = legacyHealth;
        _statusProvider = statusProvider;
        _journal = journal;
        _logError = logError;
    }

    public async Task<WorkerHttpResponse> RouteAsync(string method, string target, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedMethod = method.Trim().ToUpperInvariant();
            (string path, IReadOnlyDictionary<string, string> query) = ParseTarget(target);

            if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(normalizedMethod, "GET", StringComparison.Ordinal))
                {
                    return Error(405, "method_not_allowed", "Only GET is allowed.");
                }

                (int status, string body) = _legacyHealth(path);
                return new WorkerHttpResponse(status, ReasonPhrase(status), body);
            }

            if (!path.StartsWith("/api/v1", StringComparison.OrdinalIgnoreCase))
            {
                return Error(404, "route_not_found", "The requested route was not found.");
            }

            if (!string.Equals(normalizedMethod, "GET", StringComparison.Ordinal))
            {
                return Error(405, "method_not_allowed", "The query API is read-only; only GET is allowed.");
            }

            if (string.Equals(path, "/api/v1/status", StringComparison.OrdinalIgnoreCase))
            {
                return Success(_statusProvider());
            }

            bool journalRoute = string.Equals(path, "/api/v1/rounds", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/api/v1/rounds/", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/api/v1/events", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/api/v1/outbox", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/api/v1/recoveries", StringComparison.OrdinalIgnoreCase);
            if (!journalRoute)
            {
                return Error(404, "route_not_found", "The requested query route was not found.");
            }

            if (_journal == null)
            {
                return Error(503, "query_unavailable", "The query journal is unavailable.");
            }

            if (string.Equals(path, "/api/v1/rounds", StringComparison.OrdinalIgnoreCase))
            {
                return await QueryRoundsAsync(query).ConfigureAwait(false);
            }

            if (path.StartsWith("/api/v1/rounds/", StringComparison.OrdinalIgnoreCase))
            {
                return await QueryRoundDetailAsync(path).ConfigureAwait(false);
            }

            if (string.Equals(path, "/api/v1/events", StringComparison.OrdinalIgnoreCase))
            {
                return await QueryEventsAsync(query, outbox: false).ConfigureAwait(false);
            }

            if (string.Equals(path, "/api/v1/outbox", StringComparison.OrdinalIgnoreCase))
            {
                return await QueryEventsAsync(query, outbox: true).ConfigureAwait(false);
            }

            if (string.Equals(path, "/api/v1/recoveries", StringComparison.OrdinalIgnoreCase))
            {
                return await QueryRecoveriesAsync(query).ConfigureAwait(false);
            }

            return Error(404, "route_not_found", "The requested query route was not found.");
        }
        catch (WorkerQueryValidationException ex)
        {
            return Error(400, "invalid_query", ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"Query route failed: {ex}");
            return Error(500, "internal_error", "The query could not be completed.");
        }
    }

    private async Task<WorkerHttpResponse> QueryRoundsAsync(IReadOnlyDictionary<string, string> query)
    {
        DateTimeOffset? fromUtc = ReadDate(query, "fromUtc");
        DateTimeOffset? toUtc = ReadDate(query, "toUtc");
        ValidateDateRange(fromUtc, toUtc);
        BridgeRoundCursor? cursor = DecodeCursor<BridgeRoundCursor>(query, "rounds");
        BridgeQueryPage<BridgeRoundSummary> page = await _journal!.QueryRoundsAsync(new BridgeRoundQuery(
            Read(query, "deskCode"), fromUtc, toUtc, ReadLong(query, "shoe"), ReadLong(query, "round"),
            Read(query, "state"), ReadPageSize(query), cursor)).ConfigureAwait(false);
        string? nextCursor = page.HasMore && page.Items.Count > 0
            ? EncodeCursor("rounds", new BridgeRoundCursor(
                page.Items[^1].UpdatedUtc,
                page.Items[^1].SourceDataCode,
                page.Items[^1].Shoe,
                page.Items[^1].Round))
            : null;
        return Success(page.Items, nextCursor);
    }

    private async Task<WorkerHttpResponse> QueryRoundDetailAsync(string path)
    {
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 6 ||
            !long.TryParse(segments[4], NumberStyles.None, CultureInfo.InvariantCulture, out long shoe) ||
            !long.TryParse(segments[5], NumberStyles.None, CultureInfo.InvariantCulture, out long round))
        {
            throw new WorkerQueryValidationException("Round detail path must contain deskCode, shoe, and round.");
        }

        BridgeRoundDetail? detail = await _journal!.GetRoundDetailAsync(Uri.UnescapeDataString(segments[3]), shoe, round).ConfigureAwait(false);
        return detail == null
            ? Error(404, "round_not_found", "The requested round was not found.")
            : Success(detail);
    }

    private async Task<WorkerHttpResponse> QueryEventsAsync(IReadOnlyDictionary<string, string> query, bool outbox)
    {
        DateTimeOffset? fromUtc = ReadDate(query, "fromUtc");
        DateTimeOffset? toUtc = ReadDate(query, "toUtc");
        ValidateDateRange(fromUtc, toUtc);
        EventCursor? cursor = DecodeCursor<EventCursor>(query, outbox ? "outbox" : "events");
        string status = Read(query, "status");
        BridgeQueryPage<BridgeEventSummary> page = await _journal!.QueryEventsAsync(new BridgeStoredEventQuery(
            Read(query, "deskCode"),
            Read(query, "type"),
            status,
            fromUtc,
            toUtc,
            ReadLong(query, "shoe"),
            ReadLong(query, "round"),
            ReadPageSize(query),
            cursor?.EventId,
            !outbox && ReadBool(query, "includePayload"))).ConfigureAwait(false);
        string? nextCursor = page.HasMore && page.Items.Count > 0
            ? EncodeCursor(outbox ? "outbox" : "events", new EventCursor(page.Items[^1].EventId))
            : null;
        return Success(page.Items, nextCursor);
    }

    private async Task<WorkerHttpResponse> QueryRecoveriesAsync(IReadOnlyDictionary<string, string> query)
    {
        DateTimeOffset? fromUtc = ReadDate(query, "fromUtc");
        DateTimeOffset? toUtc = ReadDate(query, "toUtc");
        ValidateDateRange(fromUtc, toUtc);
        BridgeRecoveryCursor? cursor = DecodeCursor<BridgeRecoveryCursor>(query, "recoveries");
        BridgeQueryPage<BridgeRecoverySummary> page = await _journal!.QueryRecoveriesAsync(new BridgeRecoveryQuery(
            Read(query, "deskCode"), Read(query, "result"), Read(query, "commandType"),
            fromUtc, toUtc, ReadPageSize(query), cursor)).ConfigureAwait(false);
        string? nextCursor = page.HasMore && page.Items.Count > 0
            ? EncodeCursor("recoveries", new BridgeRecoveryCursor(page.Items[^1].LastObservedUtc, page.Items[^1].CommandId))
            : null;
        return Success(page.Items, nextCursor);
    }

    private WorkerHttpResponse Success(object data, string? nextCursor = null)
    {
        WorkerQueryEnvelope envelope = new(
            new WorkerQueryMetadata(
                "v1",
                _source.InstanceName,
                _source.Environment,
                _source.Role,
                DateTimeOffset.UtcNow),
            data,
            nextCursor);
        return new WorkerHttpResponse(200, "OK", JsonSerializer.Serialize(envelope, QueryJsonOptions));
    }

    private static WorkerHttpResponse Error(int status, string code, string message)
    {
        WorkerQueryErrorEnvelope envelope = new(new WorkerQueryError(code, message));
        return new WorkerHttpResponse(status, ReasonPhrase(status), JsonSerializer.Serialize(envelope, QueryJsonOptions));
    }

    private static (string Path, IReadOnlyDictionary<string, string> Query) ParseTarget(string target)
    {
        Uri uri = Uri.TryCreate(target, UriKind.Absolute, out Uri? absolute)
            ? absolute
            : new Uri(new Uri("http://localhost"), string.IsNullOrWhiteSpace(target) ? "/" : target);
        Dictionary<string, string> query = new(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split('=', 2);
            string key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            string value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty;
            query[key] = value;
        }
        return (string.IsNullOrWhiteSpace(uri.AbsolutePath) ? "/" : uri.AbsolutePath, query);
    }

    private static string Read(IReadOnlyDictionary<string, string> query, string key) =>
        query.TryGetValue(key, out string? value) ? value.Trim() : string.Empty;

    private static long? ReadLong(IReadOnlyDictionary<string, string> query, string key)
    {
        string value = Read(query, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) || parsed < 0)
        {
            throw new WorkerQueryValidationException($"{key} must be a non-negative integer.");
        }
        return parsed;
    }

    private static DateTimeOffset? ReadDate(IReadOnlyDictionary<string, string> query, string key)
    {
        string value = Read(query, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
        {
            throw new WorkerQueryValidationException($"{key} must be an ISO-8601 timestamp.");
        }
        return parsed.ToUniversalTime();
    }

    private static bool ReadBool(IReadOnlyDictionary<string, string> query, string key)
    {
        string value = Read(query, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        if (!bool.TryParse(value, out bool parsed))
        {
            throw new WorkerQueryValidationException($"{key} must be true or false.");
        }
        return parsed;
    }

    private static int ReadPageSize(IReadOnlyDictionary<string, string> query)
    {
        string value = Read(query, "pageSize");
        if (string.IsNullOrWhiteSpace(value))
        {
            return 50;
        }
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < 1 || parsed > 200)
        {
            throw new WorkerQueryValidationException("pageSize must be between 1 and 200.");
        }
        return parsed;
    }

    private static void ValidateDateRange(DateTimeOffset? fromUtc, DateTimeOffset? toUtc)
    {
        if (fromUtc.HasValue && toUtc.HasValue && fromUtc.Value >= toUtc.Value)
        {
            throw new WorkerQueryValidationException("fromUtc must be earlier than toUtc.");
        }
    }

    private static string EncodeCursor<T>(string kind, T value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new CursorEnvelope<T>(kind, value), QueryJsonOptions);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static T? DecodeCursor<T>(IReadOnlyDictionary<string, string> query, string kind)
    {
        string cursor = Read(query, "cursor");
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return default;
        }
        try
        {
            string base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
            CursorEnvelope<T>? envelope = JsonSerializer.Deserialize<CursorEnvelope<T>>(Convert.FromBase64String(base64), QueryJsonOptions);
            if (envelope == null || !string.Equals(envelope.Kind, kind, StringComparison.Ordinal))
            {
                throw new WorkerQueryValidationException("cursor does not match this query route.");
            }
            return envelope.Value;
        }
        catch (WorkerQueryValidationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new WorkerQueryValidationException("cursor is invalid.");
        }
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Error"
    };
}

public sealed class WorkerQueryValidationException(string message) : Exception(message);

public sealed record CursorEnvelope<T>(string Kind, T Value);

public sealed record EventCursor(long EventId);

public sealed record WorkerHttpResponse(int Status, string ReasonPhrase, string Body);

public sealed record WorkerQuerySource(string InstanceName, string Environment, string Role);

public sealed record WorkerQueryMetadata(
    string ApiVersion,
    string InstanceName,
    string Environment,
    string Role,
    DateTimeOffset GeneratedAtUtc);

public sealed record WorkerQueryEnvelope(
    WorkerQueryMetadata Meta,
    object Data,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor);

public sealed record WorkerQueryErrorEnvelope(WorkerQueryError Error);

public sealed record WorkerQueryError(string Code, string Message);

public sealed record WorkerStatusData(
    string BridgeId,
    string BridgeName,
    string Version,
    bool BmsDispatcherRunning,
    IReadOnlyList<WorkerEndpointStatusData> Endpoints);

public sealed record WorkerEndpointStatusData(
    string DeskName,
    string SourceDataCode,
    string DeviceId,
    bool Enabled,
    bool Connected,
    string Status,
    DateTimeOffset? LastEventUtc,
    string LastEvent,
    long Shoe,
    long Round,
    long? RoundId,
    bool BmsTransmitEnabled,
    int PendingCount,
    int FailedCount);
