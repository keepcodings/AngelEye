using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AngelEyeBmsBridge;

public sealed partial class BridgeEventJournal
{
    public async Task<BridgeQueryPage<BridgeRoundSummary>> QueryRoundsAsync(BridgeRoundQuery query)
    {
        List<BridgeRoundSummary> rows = [];
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        List<string> clauses = [];
        AddTextFilter(clauses, command, "r.desk_id", "$desk_id", query.SourceDataCode);
        AddInt64Filter(clauses, command, "r.shoe", "$shoe", query.Shoe);
        AddInt64Filter(clauses, command, "r.round", "$round", query.Round);
        AddTextFilter(clauses, command, "r.state", "$state", query.State);
        if (query.FromUtc.HasValue)
        {
            clauses.Add("r.updated_utc >= $from_utc");
            command.Parameters.AddWithValue("$from_utc", ToUtcText(query.FromUtc.Value));
        }
        if (query.ToUtc.HasValue)
        {
            clauses.Add("r.updated_utc < $to_utc");
            command.Parameters.AddWithValue("$to_utc", ToUtcText(query.ToUtc.Value));
        }
        if (query.Cursor != null)
        {
            clauses.Add("""
                (r.updated_utc < $cursor_updated
                 OR (r.updated_utc = $cursor_updated AND r.desk_id > $cursor_desk)
                 OR (r.updated_utc = $cursor_updated AND r.desk_id = $cursor_desk AND r.shoe < $cursor_shoe)
                 OR (r.updated_utc = $cursor_updated AND r.desk_id = $cursor_desk AND r.shoe = $cursor_shoe AND r.round < $cursor_round))
                """);
            command.Parameters.AddWithValue("$cursor_updated", query.Cursor.UpdatedUtc);
            command.Parameters.AddWithValue("$cursor_desk", query.Cursor.SourceDataCode);
            command.Parameters.AddWithValue("$cursor_shoe", query.Cursor.Shoe);
            command.Parameters.AddWithValue("$cursor_round", query.Cursor.Round);
        }

        string where = clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
        command.CommandText = $"""
            SELECT r.desk_id, r.device_id, r.shoe, r.round, r.round_id,
                   r.started_utc, r.settled_utc, r.state, r.cards_json, r.result_json,
                   r.updated_utc, r.is_complete,
                   e.status, e.retry_count, e.last_attempt_utc, e.sent_utc, e.last_error,
                   (SELECT a.http_status FROM bridge_delivery_attempts a
                    WHERE a.event_id = COALESCE(r.result_event_id, r.start_event_id)
                    ORDER BY a.attempt_id DESC LIMIT 1)
            FROM bridge_rounds r
            LEFT JOIN bridge_events e ON e.event_id = COALESCE(r.result_event_id, r.start_event_id)
            {where}
            ORDER BY r.updated_utc DESC, r.desk_id ASC, r.shoe DESC, r.round DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", query.PageSize + 1);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(ReadRoundSummary(reader));
        }

        bool hasMore = rows.Count > query.PageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        return new BridgeQueryPage<BridgeRoundSummary>(rows, hasMore);
    }

    public async Task<BridgeRoundDetail?> GetRoundDetailAsync(string sourceDataCode, long shoe, long round)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        BridgeRoundSummary? summary;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT r.desk_id, r.device_id, r.shoe, r.round, r.round_id,
                       r.started_utc, r.settled_utc, r.state, r.cards_json, r.result_json,
                       r.updated_utc, r.is_complete,
                       e.status, e.retry_count, e.last_attempt_utc, e.sent_utc, e.last_error,
                       (SELECT a.http_status FROM bridge_delivery_attempts a
                        WHERE a.event_id = COALESCE(r.result_event_id, r.start_event_id)
                        ORDER BY a.attempt_id DESC LIMIT 1)
                FROM bridge_rounds r
                LEFT JOIN bridge_events e ON e.event_id = COALESCE(r.result_event_id, r.start_event_id)
                WHERE r.desk_id = $desk_id AND r.shoe = $shoe AND r.round = $round;
                """;
            command.Parameters.AddWithValue("$desk_id", sourceDataCode);
            command.Parameters.AddWithValue("$shoe", shoe);
            command.Parameters.AddWithValue("$round", round);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            summary = await reader.ReadAsync().ConfigureAwait(false) ? ReadRoundSummary(reader) : null;
        }

        if (summary == null)
        {
            return null;
        }

        List<BridgeEventSummary> timeline = [];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT event_id, occurred_utc, type, desk_id, device_id, shoe, round, round_id,
                       status, retry_count, next_retry_utc, last_attempt_utc, sent_utc, last_error,
                       (SELECT a.http_status FROM bridge_delivery_attempts a
                        WHERE a.event_id = bridge_events.event_id ORDER BY a.attempt_id DESC LIMIT 1),
                       NULL
                FROM bridge_events
                WHERE desk_id = $desk_id AND shoe = $shoe AND round = $round
                ORDER BY occurred_utc ASC, event_id ASC;
                """;
            command.Parameters.AddWithValue("$desk_id", sourceDataCode);
            command.Parameters.AddWithValue("$shoe", shoe);
            command.Parameters.AddWithValue("$round", round);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                timeline.Add(ReadEventSummary(reader));
            }
        }
        return new BridgeRoundDetail(summary, timeline);
    }

    public async Task<BridgeQueryPage<BridgeEventSummary>> QueryEventsAsync(BridgeStoredEventQuery query)
    {
        List<BridgeEventSummary> rows = [];
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        List<string> clauses = [];
        AddTextFilter(clauses, command, "desk_id", "$desk_id", query.SourceDataCode);
        AddTextFilter(clauses, command, "type", "$type", query.Type);
        AddTextFilter(clauses, command, "status", "$status", query.Status);
        AddInt64Filter(clauses, command, "shoe", "$shoe", query.Shoe);
        AddInt64Filter(clauses, command, "round", "$round", query.Round);
        if (query.FromUtc.HasValue)
        {
            clauses.Add("occurred_utc >= $from_utc");
            command.Parameters.AddWithValue("$from_utc", ToUtcText(query.FromUtc.Value));
        }
        if (query.ToUtc.HasValue)
        {
            clauses.Add("occurred_utc < $to_utc");
            command.Parameters.AddWithValue("$to_utc", ToUtcText(query.ToUtc.Value));
        }
        if (query.BeforeEventId.HasValue)
        {
            clauses.Add("event_id < $before_event_id");
            command.Parameters.AddWithValue("$before_event_id", query.BeforeEventId.Value);
        }

        string where = clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
        string payloadColumn = query.IncludePayload ? "payload_json" : "NULL";
        command.CommandText = $"""
            SELECT event_id, occurred_utc, type, desk_id, device_id, shoe, round, round_id,
                   status, retry_count, next_retry_utc, last_attempt_utc, sent_utc, last_error,
                   (SELECT a.http_status FROM bridge_delivery_attempts a
                    WHERE a.event_id = bridge_events.event_id ORDER BY a.attempt_id DESC LIMIT 1),
                   {payloadColumn}
            FROM bridge_events
            {where}
            ORDER BY event_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", query.PageSize + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(ReadEventSummary(reader));
        }

        bool hasMore = rows.Count > query.PageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        return new BridgeQueryPage<BridgeEventSummary>(rows, hasMore);
    }

    public async Task<BridgeQueryPage<BridgeRecoverySummary>> QueryRecoveriesAsync(BridgeRecoveryQuery query)
    {
        List<BridgeRecoverySummary> rows = [];
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        List<string> clauses = [];
        AddTextFilter(clauses, command, "desk_id", "$desk_id", query.SourceDataCode);
        AddTextFilter(clauses, command, "result", "$result", query.Result);
        AddTextFilter(clauses, command, "command_type", "$command_type", query.CommandType);
        if (query.FromUtc.HasValue)
        {
            clauses.Add("last_observed_utc >= $from_utc");
            command.Parameters.AddWithValue("$from_utc", ToUtcText(query.FromUtc.Value));
        }
        if (query.ToUtc.HasValue)
        {
            clauses.Add("last_observed_utc < $to_utc");
            command.Parameters.AddWithValue("$to_utc", ToUtcText(query.ToUtc.Value));
        }
        if (query.Cursor != null)
        {
            clauses.Add("(last_observed_utc < $cursor_observed OR (last_observed_utc = $cursor_observed AND command_id > $cursor_id))");
            command.Parameters.AddWithValue("$cursor_observed", query.Cursor.ObservedUtc);
            command.Parameters.AddWithValue("$cursor_id", query.Cursor.CommandId);
        }
        string where = clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
        command.CommandText = $"""
            SELECT command_id, command_type, desk_id, device_id, shoe, round, round_id,
                   received_utc, last_observed_utc, result, next_retry_utc, message
            FROM bridge_recovery_requests
            {where}
            ORDER BY last_observed_utc DESC, command_id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", query.PageSize + 1);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            rows.Add(new BridgeRecoverySummary(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                ReadNullableInt64(reader, 4), ReadNullableInt64(reader, 5), ReadNullableInt64(reader, 6),
                reader.GetString(7), reader.GetString(8), reader.GetString(9),
                ReadNullableString(reader, 10), ReadNullableString(reader, 11)));
        }
        bool hasMore = rows.Count > query.PageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        return new BridgeQueryPage<BridgeRecoverySummary>(rows, hasMore);
    }

    private static BridgeRoundSummary ReadRoundSummary(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
        ReadNullableInt64(reader, 4), ReadNullableString(reader, 5), ReadNullableString(reader, 6),
        reader.GetString(7), ReadNullableString(reader, 8), ReadNullableString(reader, 9), reader.GetString(10),
        reader.GetInt32(11) == 1, ReadNullableString(reader, 12),
        reader.IsDBNull(13) ? 0 : reader.GetInt32(13), ReadNullableString(reader, 14),
        ReadNullableString(reader, 15), ReadNullableString(reader, 16),
        reader.IsDBNull(17) ? null : reader.GetInt32(17));

    private static BridgeEventSummary ReadEventSummary(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.GetInt64(5), reader.GetInt64(6), ReadNullableInt64(reader, 7), reader.GetString(8), reader.GetInt32(9),
        ReadNullableString(reader, 10), ReadNullableString(reader, 11), ReadNullableString(reader, 12),
        ReadNullableString(reader, 13), reader.IsDBNull(14) ? null : reader.GetInt32(14),
        reader.IsDBNull(15) ? null : RedactPayload(reader.GetString(15)));

    private static string RedactPayload(string json)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(json);
            RedactNode(node);
            return node?.ToJsonString() ?? "null";
        }
        catch (JsonException)
        {
            return "[INVALID JSON REDACTED]";
        }
    }

    private static void RedactNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (string key in obj.Select(pair => pair.Key).ToList())
            {
                string normalized = key.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
                if (normalized.Contains("signingkey", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Contains("jwt", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("token", StringComparison.OrdinalIgnoreCase))
                {
                    obj[key] = "[REDACTED]";
                }
                else
                {
                    RedactNode(obj[key]);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                RedactNode(child);
            }
        }
    }

    private static void AddTextFilter(List<string> clauses, SqliteCommand command, string column, string parameter, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            clauses.Add($"{column} = {parameter}");
            command.Parameters.AddWithValue(parameter, value);
        }
    }

    private static void AddInt64Filter(List<string> clauses, SqliteCommand command, string column, string parameter, long? value)
    {
        if (value.HasValue)
        {
            clauses.Add($"{column} = {parameter}");
            command.Parameters.AddWithValue(parameter, value.Value);
        }
    }

    private static string ToUtcText(DateTimeOffset value) => value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);
    private static long? ReadNullableInt64(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt64(index);
    private static string? ReadNullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
}

public sealed record BridgeQueryPage<T>(IReadOnlyList<T> Items, bool HasMore);
public sealed record BridgeRoundCursor(string UpdatedUtc, string SourceDataCode, long Shoe, long Round);
public sealed record BridgeRoundQuery(string SourceDataCode, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, long? Shoe, long? Round, string State, int PageSize, BridgeRoundCursor? Cursor);
public sealed record BridgeRoundSummary(string SourceDataCode, string DeviceId, long Shoe, long Round, long? RoundId, string? StartedUtc, string? SettledUtc, string State, string? CardsJson, string? ResultJson, string UpdatedUtc, bool IsComplete, string? DeliveryStatus, int RetryCount, string? LastAttemptUtc, string? SentUtc, string? LastError, int? HttpStatus);
public sealed record BridgeRoundDetail(BridgeRoundSummary Round, IReadOnlyList<BridgeEventSummary> Timeline);
public sealed record BridgeStoredEventQuery(string SourceDataCode, string Type, string Status, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, long? Shoe, long? Round, int PageSize, long? BeforeEventId, bool IncludePayload);
public sealed record BridgeEventSummary(long EventId, string OccurredUtc, string Type, string SourceDataCode, string DeviceId, long Shoe, long Round, long? RoundId, string Status, int RetryCount, string? NextRetryUtc, string? LastAttemptUtc, string? SentUtc, string? LastError, int? HttpStatus, string? PayloadJson);
public sealed record BridgeRecoveryCursor(string ObservedUtc, string CommandId);
public sealed record BridgeRecoveryQuery(string SourceDataCode, string Result, string CommandType, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, int PageSize, BridgeRecoveryCursor? Cursor);
public sealed record BridgeRecoverySummary(string CommandId, string CommandType, string SourceDataCode, string DeviceId, long? Shoe, long? Round, long? RoundId, string ReceivedUtc, string LastObservedUtc, string Result, string? NextRetryUtc, string? Message);
