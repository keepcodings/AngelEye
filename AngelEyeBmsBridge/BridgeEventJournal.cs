using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace AngelEyeBmsBridge;

/// <summary>
/// Maintains the local SQLite event journal and retry outbox for bridge-to-BMS delivery.
/// </summary>
public sealed class BridgeEventJournal
{
    private long _nextEventId;

    /// <summary>
    /// Creates or opens a bridge event journal.
    /// </summary>
    /// <param name="dbPath">Optional SQLite database path; defaults to the application folder.</param>
    public BridgeEventJournal(string? dbPath = null)
    {
        DbPath = string.IsNullOrWhiteSpace(dbPath)
            ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bridge-events.sqlite")
            : dbPath;

        Initialize();
        _nextEventId = GetMaxEventId();
    }

    /// <summary>SQLite database path used by this journal.</summary>
    public string DbPath { get; }

    /// <summary>
    /// Appends an event payload and assigns the next local event ID.
    /// </summary>
    /// <param name="payload">Mutable event payload that receives the generated eventId.</param>
    /// <returns>The generated event ID.</returns>
    public async Task<long> AppendAsync(Dictionary<string, object?> payload)
    {
        long eventId = Interlocked.Increment(ref _nextEventId);
        payload["eventId"] = eventId;

        string payloadJson = JsonSerializer.Serialize(payload);
        string sourceDeskCode = GetString(payload, "sourceDataCode");

        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO bridge_events
                (event_id, occurred_utc, type, source, desk_id, device_id, shoe, round, round_id, payload_json)
            VALUES
                ($event_id, $occurred_utc, $type, $source, $desk_id, $device_id, $shoe, $round, $round_id, $payload_json);
            """;

        command.Parameters.AddWithValue("$event_id", eventId);
        command.Parameters.AddWithValue("$occurred_utc", GetString(payload, "timestamp"));
        command.Parameters.AddWithValue("$type", GetString(payload, "type"));
        command.Parameters.AddWithValue("$source", GetString(payload, "source"));
        command.Parameters.AddWithValue("$desk_id", sourceDeskCode);
        command.Parameters.AddWithValue("$device_id", GetString(payload, "deviceId", GetString(payload, "shoeId")));
        command.Parameters.AddWithValue("$shoe", GetInt64(payload, "shoe"));
        command.Parameters.AddWithValue("$round", GetInt64(payload, "round"));

        long? roundId = GetNullableInt64(payload, "roundId");
        command.Parameters.AddWithValue("$round_id", roundId.HasValue ? roundId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$payload_json", payloadJson);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        return eventId;
    }

    /// <summary>
    /// Reads pending or retry-due events for API delivery.
    /// </summary>
    /// <param name="limit">Maximum number of events to return.</param>
    /// <param name="utcNow">Current UTC time used for retry scheduling.</param>
    /// <returns>Pending events ordered by event ID.</returns>
    public async Task<List<BridgePendingEvent>> GetDueOutboxEventsAsync(int limit, DateTime utcNow)
    {
        List<BridgePendingEvent> events = [];

        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.event_id, e.type, e.desk_id, e.device_id, e.shoe, e.round, e.payload_json, e.retry_count
            FROM bridge_events e
            WHERE e.status <> 'Sent'
              AND (e.next_retry_utc IS NULL OR e.next_retry_utc <= $now)
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM bridge_events older
                  WHERE older.status <> 'Sent'
                    AND older.desk_id = e.desk_id
                    AND older.device_id = e.device_id
                    AND older.event_id < e.event_id
              )
            ORDER BY e.event_id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", utcNow.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            events.Add(new BridgePendingEvent(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.GetInt32(7)));
        }

        return events;
    }

    /// <summary>
    /// Reads delivery health for one endpoint's local outbox.
    /// </summary>
    /// <param name="sourceDataCode">BMS source table code.</param>
    /// <param name="deviceId">Bridge shoe device identifier.</param>
    /// <returns>Current pending and failure summary.</returns>
    public async Task<BridgeOutboxStatus> GetOutboxStatusAsync(string sourceDataCode, string deviceId)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        int pendingCount = 0;
        int failedCount = 0;
        int maxRetryCount = 0;
        DateTime? oldestFailedAttemptUtc = null;
        await using (SqliteCommand summary = connection.CreateCommand())
        {
            summary.CommandText = """
                SELECT COUNT(*),
                       IFNULL(SUM(CASE WHEN status = 'Failed' THEN 1 ELSE 0 END), 0),
                       IFNULL(MAX(retry_count), 0),
                       MIN(CASE WHEN status = 'Failed' THEN last_attempt_utc ELSE NULL END)
                FROM bridge_events
                WHERE status <> 'Sent'
                  AND desk_id = $desk_id
                  AND device_id = $device_id;
                """;
            summary.Parameters.AddWithValue("$desk_id", sourceDataCode);
            summary.Parameters.AddWithValue("$device_id", deviceId);

            await using SqliteDataReader reader = await summary.ExecuteReaderAsync().ConfigureAwait(false);
            if (await reader.ReadAsync().ConfigureAwait(false))
            {
                pendingCount = ToInt32(reader.GetInt64(0));
                failedCount = ToInt32(reader.GetInt64(1));
                maxRetryCount = ToInt32(reader.GetInt64(2));
                oldestFailedAttemptUtc = reader.IsDBNull(3) ? null : ParseUtc(reader.GetString(3));
            }
        }

        string lastError = string.Empty;
        DateTime? lastAttemptUtc = null;
        await using (SqliteCommand latestFailure = connection.CreateCommand())
        {
            latestFailure.CommandText = """
                SELECT last_error, last_attempt_utc
                FROM bridge_events
                WHERE status = 'Failed'
                  AND desk_id = $desk_id
                  AND device_id = $device_id
                  AND last_error IS NOT NULL
                  AND last_error <> ''
                ORDER BY last_attempt_utc DESC, event_id DESC
                LIMIT 1;
                """;
            latestFailure.Parameters.AddWithValue("$desk_id", sourceDataCode);
            latestFailure.Parameters.AddWithValue("$device_id", deviceId);

            await using SqliteDataReader reader = await latestFailure.ExecuteReaderAsync().ConfigureAwait(false);
            if (await reader.ReadAsync().ConfigureAwait(false))
            {
                lastError = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                lastAttemptUtc = reader.IsDBNull(1) ? null : ParseUtc(reader.GetString(1));
            }
        }

        return new BridgeOutboxStatus(
            pendingCount,
            failedCount,
            maxRetryCount,
            oldestFailedAttemptUtc,
            lastAttemptUtc,
            lastError);
    }

    /// <summary>
    /// Marks an outbox event as successfully delivered.
    /// </summary>
    /// <param name="eventId">Local event ID.</param>
    /// <param name="utcNow">Delivery time in UTC.</param>
    public async Task MarkSentAsync(long eventId, DateTime utcNow)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE bridge_events
            SET status = 'Sent',
                sent_utc = $now,
                last_attempt_utc = $now,
                next_retry_utc = NULL,
                last_error = NULL
            WHERE event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        command.Parameters.AddWithValue("$now", utcNow.ToString("o", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Marks an outbox delivery attempt as failed and schedules the next retry.
    /// </summary>
    /// <param name="eventId">Local event ID.</param>
    /// <param name="retryCount">Updated retry count.</param>
    /// <param name="utcNow">Failure time in UTC.</param>
    /// <param name="error">Failure detail to store.</param>
    public async Task MarkFailedAsync(long eventId, int retryCount, DateTime utcNow, string error)
    {
        DateTime nextRetry = utcNow.Add(CalculateBackoff(retryCount));

        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE bridge_events
            SET status = 'Failed',
                retry_count = $retry_count,
                last_attempt_utc = $now,
                next_retry_utc = $next_retry,
                last_error = $last_error
            WHERE event_id = $event_id
              AND status <> 'Sent';
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        command.Parameters.AddWithValue("$retry_count", retryCount);
        command.Parameters.AddWithValue("$now", utcNow.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$next_retry", nextRetry.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$last_error", TrimForStore(error));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Requeues one stored event for delivery when BMS explicitly asks the bridge to resend it.
    /// </summary>
    /// <param name="eventId">Local event ID to resend.</param>
    /// <param name="utcNow">Current UTC time used as the retry due time.</param>
    /// <param name="reason">Audit reason stored in the last-error field until the send succeeds.</param>
    /// <returns>The number of rows requeued.</returns>
    public async Task<int> RequeueEventAsync(long eventId, DateTime utcNow, string reason)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE bridge_events
            SET status = 'Pending',
                sent_utc = NULL,
                next_retry_utc = $now,
                last_error = $reason
            WHERE event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        command.Parameters.AddWithValue("$now", utcNow.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$reason", TrimForStore(reason));
        return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Requeues stored events matching a recovery or resend query.
    /// </summary>
    /// <param name="query">Filter and paging options; the newest matching rows are requeued first.</param>
    /// <param name="utcNow">Current UTC time used as the retry due time.</param>
    /// <param name="reason">Audit reason stored in the last-error field until the send succeeds.</param>
    /// <returns>The number of rows requeued.</returns>
    public async Task<int> RequeueMatchingEventsAsync(BridgeEventQuery query, DateTime utcNow, string reason)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        string where = BuildWhereClause(query, command);
        if (string.IsNullOrWhiteSpace(where))
        {
            return 0;
        }

        command.CommandText = $"""
            UPDATE bridge_events
            SET status = 'Pending',
                sent_utc = NULL,
                next_retry_utc = $now,
                last_error = $reason
            WHERE event_id IN
            (
                SELECT event_id
                FROM bridge_events
                {where}
                ORDER BY event_id DESC
                LIMIT $limit
            );
            """;
        command.Parameters.AddWithValue("$now", utcNow.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$reason", TrimForStore(reason));
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 500));
        return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Queries stored payload JSON values for diagnostics or replay.
    /// </summary>
    /// <param name="query">Filter and paging options.</param>
    /// <returns>Payload JSON rows ordered by event ID.</returns>
    public async Task<List<string>> QueryPayloadJsonAsync(BridgeEventQuery query)
    {
        List<string> results = [];

        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        string where = BuildWhereClause(query, command);
        command.CommandText = $"""
            SELECT payload_json
            FROM bridge_events
            {where}
            ORDER BY event_id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 500));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    private static string BuildWhereClause(BridgeEventQuery query, SqliteCommand command)
    {
        List<string> clauses = [];
        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            clauses.Add("type = $type");
            command.Parameters.AddWithValue("$type", query.Type);
        }

        if (!string.IsNullOrWhiteSpace(query.SourceDataCode))
        {
            clauses.Add("desk_id = $desk_id");
            command.Parameters.AddWithValue("$desk_id", query.SourceDataCode);
        }

        if (!string.IsNullOrWhiteSpace(query.DeviceId))
        {
            clauses.Add("device_id = $device_id");
            command.Parameters.AddWithValue("$device_id", query.DeviceId);
        }

        if (query.Shoe.HasValue)
        {
            clauses.Add("shoe = $shoe");
            command.Parameters.AddWithValue("$shoe", query.Shoe.Value);
        }

        if (query.Round.HasValue)
        {
            clauses.Add("round = $round");
            command.Parameters.AddWithValue("$round", query.Round.Value);
        }

        if (query.RoundId.HasValue)
        {
            clauses.Add("round_id = $round_id");
            command.Parameters.AddWithValue("$round_id", query.RoundId.Value);
        }

        if (query.AfterId.HasValue)
        {
            clauses.Add("event_id > $after_id");
            command.Parameters.AddWithValue("$after_id", query.AfterId.Value);
        }

        return clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
    }

    private void Initialize()
    {
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS bridge_events
            (
                event_id INTEGER PRIMARY KEY,
                occurred_utc TEXT NOT NULL,
                type TEXT NOT NULL,
                source TEXT NOT NULL,
                desk_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                shoe INTEGER NOT NULL,
                round INTEGER NOT NULL,
                round_id INTEGER NULL,
                payload_json TEXT NOT NULL
            );
            """);
        EnsureColumn(connection, "bridge_events", "status", "status TEXT NOT NULL DEFAULT 'Pending'");
        EnsureColumn(connection, "bridge_events", "retry_count", "retry_count INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "bridge_events", "next_retry_utc", "next_retry_utc TEXT NULL");
        EnsureColumn(connection, "bridge_events", "last_attempt_utc", "last_attempt_utc TEXT NULL");
        EnsureColumn(connection, "bridge_events", "sent_utc", "sent_utc TEXT NULL");
        EnsureColumn(connection, "bridge_events", "last_error", "last_error TEXT NULL");
        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS ix_bridge_events_lookup
                ON bridge_events (desk_id, shoe, round, type, event_id);
            """);
        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS ix_bridge_events_device
                ON bridge_events (device_id, event_id);
            """);
        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS ix_bridge_events_occurred
                ON bridge_events (occurred_utc);
            """);
        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS ix_bridge_events_outbox
                ON bridge_events (status, next_retry_utc, event_id);
            """);
        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS ix_bridge_events_outbox_endpoint_order
                ON bridge_events (desk_id, device_id, status, event_id);
            """);
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, string tableName, string columnName, string definition)
    {
        using SqliteCommand check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({tableName});";
        using SqliteDataReader reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using SqliteCommand alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {definition};";
        alter.ExecuteNonQuery();
    }

    private long GetMaxEventId()
    {
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT IFNULL(MAX(event_id), 0) FROM bridge_events;";
        object? result = command.ExecuteScalar();
        return result is long value ? value : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private SqliteConnection CreateConnection()
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        };
        return new SqliteConnection(builder.ToString());
    }

    private static string GetString(Dictionary<string, object?> payload, string key, string fallback = "")
    {
        return payload.TryGetValue(key, out object? value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback : fallback;
    }

    private static long GetInt64(Dictionary<string, object?> payload, string key)
    {
        return GetNullableInt64(payload, key) ?? 0;
    }

    private static long? GetNullableInt64(Dictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out object? value) || value == null)
        {
            return null;
        }

        return value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            short shortValue => shortValue,
            byte byteValue => byteValue,
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
        };
    }

    private static TimeSpan CalculateBackoff(int retryCount)
    {
        int seconds = retryCount switch
        {
            <= 1 => 2,
            2 => 5,
            3 => 15,
            4 => 30,
            5 => 60,
            _ => 120
        };
        return TimeSpan.FromSeconds(seconds);
    }

    private static string TrimForStore(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = text.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    private static int ToInt32(long value)
    {
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    private static DateTime? ParseUtc(string text)
    {
        return DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTime parsed)
            ? parsed
            : null;
    }
}

/// <summary>
/// Represents one event waiting for BMS API delivery.
/// </summary>
/// <param name="EventId">Local event ID.</param>
/// <param name="Type">Bridge event type.</param>
/// <param name="SourceDataCode">BMS source table code stored in the legacy desk column.</param>
/// <param name="DeviceId">Bridge shoe device identifier.</param>
/// <param name="Shoe">BMS shoe number.</param>
/// <param name="Round">BMS round number.</param>
/// <param name="PayloadJson">Serialized event payload.</param>
/// <param name="RetryCount">Number of failed delivery attempts.</param>
public sealed record BridgePendingEvent(
    long EventId,
    string Type,
    string SourceDataCode,
    string DeviceId,
    long Shoe,
    long Round,
    string PayloadJson,
    int RetryCount);

/// <summary>
/// Summarizes local outbox delivery health for one endpoint.
/// </summary>
/// <param name="PendingCount">Number of events not yet accepted by BMS.</param>
/// <param name="FailedCount">Number of pending events with at least one failed send attempt.</param>
/// <param name="MaxRetryCount">Highest retry count among pending events.</param>
/// <param name="OldestFailedAttemptUtc">Oldest failed attempt time among currently pending events.</param>
/// <param name="LastAttemptUtc">Latest failed attempt time.</param>
/// <param name="LastError">Latest delivery error detail.</param>
public sealed record BridgeOutboxStatus(
    int PendingCount,
    int FailedCount,
    int MaxRetryCount,
    DateTime? OldestFailedAttemptUtc,
    DateTime? LastAttemptUtc,
    string LastError)
{
    /// <summary>Empty status with no pending events.</summary>
    public static BridgeOutboxStatus Empty { get; } = new(0, 0, 0, null, null, string.Empty);
}

/// <summary>
/// Filter options for reading stored bridge event payloads.
/// </summary>
public sealed class BridgeEventQuery
{
    /// <summary>Optional event type filter.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Optional BMS source table code filter.</summary>
    public string SourceDataCode { get; init; } = string.Empty;

    /// <summary>Optional bridge shoe device identifier filter.</summary>
    public string DeviceId { get; init; } = string.Empty;

    /// <summary>Optional BMS shoe number filter.</summary>
    public long? Shoe { get; init; }

    /// <summary>Optional BMS round number filter.</summary>
    public long? Round { get; init; }

    /// <summary>Optional bridge round identifier filter.</summary>
    public long? RoundId { get; init; }

    /// <summary>Only return events after this local event ID.</summary>
    public long? AfterId { get; init; }

    /// <summary>Maximum rows to return.</summary>
    public int Limit { get; init; } = 100;
}
