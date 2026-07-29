using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AngelEyeBmsBridge;

/// <summary>
/// Maintains the local SQLite event journal and retry outbox for bridge-to-BMS delivery.
/// </summary>
public sealed partial class BridgeEventJournal
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
    /// <param name="queueForDelivery">
    /// Whether this event is eligible for the one-shot BMS delivery outbox.
    /// Diagnostic events remain queryable with a LocalOnly status.
    /// </param>
    /// <returns>The generated event ID.</returns>
    public async Task<long> AppendAsync(
        Dictionary<string, object?> payload,
        bool queueForDelivery = true)
    {
        Guid eventUid = GetOrCreateEventUid(payload);
        payload["eventUid"] = eventUid;
        string type = GetString(payload, "type");
        string sourceDeskCode = GetString(payload, "sourceDataCode");
        bool deliveryEligible = queueForDelivery && IsBmsDeliveryEvent(type);

        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();

        (long EventId, string PayloadJson)? existing =
            await FindEventByUidAsync(connection, transaction, eventUid)
                .ConfigureAwait(false);
        if (existing.HasValue)
        {
            string candidateJson = JsonSerializer.Serialize(payload);
            if (!HasEquivalentStablePayload(
                    existing.Value.PayloadJson,
                    candidateJson))
            {
                throw new InvalidDataException(
                    $"EventUid {eventUid:D} already exists with a different stable payload.");
            }

            transaction.Commit();
            payload["eventId"] = existing.Value.EventId;
            return existing.Value.EventId;
        }

        long eventId = Interlocked.Increment(ref _nextEventId);
        payload["eventId"] = eventId;
        string payloadJson = JsonSerializer.Serialize(payload);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO bridge_events
                (event_id, event_uid, occurred_utc, type, source, desk_id, device_id, shoe, round, round_id, payload_json, status)
            VALUES
                ($event_id, $event_uid, $occurred_utc, $type, $source, $desk_id, $device_id, $shoe, $round, $round_id, $payload_json, $status);
            """;

        command.Parameters.AddWithValue("$event_id", eventId);
        command.Parameters.AddWithValue("$event_uid", eventUid.ToString("D"));
        command.Parameters.AddWithValue("$occurred_utc", GetString(payload, "timestamp"));
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$source", GetString(payload, "source"));
        command.Parameters.AddWithValue("$desk_id", sourceDeskCode);
        command.Parameters.AddWithValue("$device_id", GetString(payload, "deviceId", GetString(payload, "shoeId")));
        command.Parameters.AddWithValue("$shoe", GetInt64(payload, "shoe"));
        command.Parameters.AddWithValue("$round", GetInt64(payload, "round"));

        long? roundId = GetNullableInt64(payload, "roundId");
        command.Parameters.AddWithValue("$round_id", roundId.HasValue ? roundId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$payload_json", payloadJson);
        command.Parameters.AddWithValue("$status", deliveryEligible ? "Pending" : "LocalOnly");

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        await UpdateRoundProjectionAsync(connection, transaction, payload, payloadJson, eventId).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        return eventId;
    }

    private static async Task<(long EventId, string PayloadJson)?> FindEventByUidAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid eventUid)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT event_id, payload_json
            FROM bridge_events
            WHERE event_uid = $event_uid COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$event_uid", eventUid.ToString("D"));
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync().ConfigureAwait(false);
        return await reader.ReadAsync().ConfigureAwait(false)
            ? (reader.GetInt64(0), reader.GetString(1))
            : null;
    }

    private static bool HasEquivalentStablePayload(
        string storedJson,
        string candidateJson)
    {
        try
        {
            using JsonDocument stored = JsonDocument.Parse(storedJson);
            using JsonDocument candidate = JsonDocument.Parse(candidateJson);
            return string.Equals(
                CanonicalizeStablePayload(stored.RootElement),
                CanonicalizeStablePayload(candidate.RootElement),
                StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string CanonicalizeStablePayload(JsonElement root)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            WriteCanonicalJson(writer, root, path: string.Empty);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonicalJson(
        Utf8JsonWriter writer,
        JsonElement element,
        string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element
                             .EnumerateObject()
                             .OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    bool rootVolatile =
                        path.Length == 0 &&
                        property.Name is "eventId" or "sequence" or "timestamp";
                    bool resultObservationTime =
                        string.Equals(path, "data", StringComparison.Ordinal) &&
                        string.Equals(
                            property.Name,
                            "sourceTimestamp",
                            StringComparison.Ordinal);
                    if (rootVolatile || resultObservationTime)
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    string childPath = path.Length == 0
                        ? property.Name
                        : $"{path}.{property.Name}";
                    WriteCanonicalJson(writer, property.Value, childPath);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement child in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, child, path);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// Durably stores raw serial/TCP bytes independently from BMS event payloads.
    /// Raw protocol data is local diagnostic evidence and is never queued for delivery.
    /// </summary>
    public async Task<long> AppendRawFrameAsync(
        string sourceDataCode,
        string deviceId,
        long shoe,
        long round,
        long? roundId,
        string direction,
        string rawHex,
        DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(sourceDataCode) ||
            string.IsNullOrWhiteSpace(deviceId) ||
            string.IsNullOrWhiteSpace(direction) ||
            string.IsNullOrWhiteSpace(rawHex))
        {
            throw new ArgumentException("Raw frame identity, direction, and payload are required.");
        }

        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO bridge_raw_frames
                (occurred_utc, desk_id, device_id, shoe, round, round_id, direction, raw_hex)
            VALUES
                ($occurred_utc, $desk_id, $device_id, $shoe, $round, $round_id, $direction, $raw_hex);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue(
            "$occurred_utc",
            observedAtUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$desk_id", sourceDataCode.Trim());
        command.Parameters.AddWithValue("$device_id", deviceId.Trim());
        command.Parameters.AddWithValue("$shoe", shoe);
        command.Parameters.AddWithValue("$round", round);
        command.Parameters.AddWithValue("$round_id", roundId.HasValue ? roundId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$direction", direction.Trim());
        command.Parameters.AddWithValue("$raw_hex", rawHex.Trim());
        object? scalar = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task UpdateRoundProjectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Dictionary<string, object?> payload,
        string payloadJson,
        long eventId,
        bool allowLegacyCardContract = false)
    {
        string type = GetString(payload, "type");
        if (type is not ("StartGame" or "CardDrawn" or "GameResult"))
        {
            return;
        }

        string deskId = GetString(payload, "sourceDataCode");
        string deviceId = GetString(payload, "deviceId", GetString(payload, "shoeId"));
        long shoe = GetInt64(payload, "shoe");
        long round = GetInt64(payload, "round");
        long? roundId = GetNullableInt64(payload, "roundId");
        string occurredUtc = NormalizeUtcText(GetString(payload, "timestamp"));

        using JsonDocument document = JsonDocument.Parse(payloadJson);
        JsonElement data = document.RootElement.TryGetProperty("data", out JsonElement dataElement)
            ? dataElement
            : default;

        if (type == "StartGame")
        {
            string startedUtc = data.ValueKind == JsonValueKind.Object &&
                data.TryGetProperty("startTime", out JsonElement startTimeElement)
                    ? NormalizeUtcText(startTimeElement.GetString() ?? occurredUtc)
                    : occurredUtc;
            await UpsertStartGameAsync(
                connection,
                transaction,
                deskId,
                deviceId,
                shoe,
                round,
                roundId,
                startedUtc,
                occurredUtc,
                eventId).ConfigureAwait(false);
            return;
        }

        if (type == "CardDrawn")
        {
            string eventCode = ReadJsonString(data, "eventCode");
            bool accepted = ReadJsonBoolean(data, "accepted");
            bool legacyAccepted =
                allowLegacyCardContract &&
                string.IsNullOrWhiteSpace(eventCode) &&
                !HasJsonProperty(data, "accepted");
            if ((!string.Equals(eventCode, "D", StringComparison.Ordinal) ||
                 !accepted) &&
                !legacyAccepted)
            {
                return;
            }

            await UpsertCardDrawnAsync(
                connection,
                transaction,
                deskId,
                deviceId,
                shoe,
                round,
                roundId,
                occurredUtc,
                eventId,
                data).ConfigureAwait(false);
            return;
        }

        string resultJson = data.ValueKind == JsonValueKind.Undefined ? "null" : data.GetRawText();
        await UpsertGameResultAsync(
            connection,
            transaction,
            deskId,
            deviceId,
            shoe,
            round,
            roundId,
            occurredUtc,
            eventId,
            resultJson).ConfigureAwait(false);
    }

    private static async Task UpsertStartGameAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deskId,
        string deviceId,
        long shoe,
        long round,
        long? roundId,
        string startedUtc,
        string updatedUtc,
        long eventId)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO bridge_rounds
                (desk_id, device_id, shoe, round, round_id, started_utc, state, start_event_id, updated_utc, is_complete)
            VALUES
                ($desk_id, $device_id, $shoe, $round, $round_id, $started_utc, 'Started', $event_id, $updated_utc, 0)
            ON CONFLICT (desk_id, shoe, round) DO UPDATE SET
                device_id = excluded.device_id,
                round_id = COALESCE(bridge_rounds.round_id, excluded.round_id),
                started_utc = COALESCE(bridge_rounds.started_utc, excluded.started_utc),
                start_event_id = COALESCE(bridge_rounds.start_event_id, excluded.start_event_id),
                state = CASE WHEN bridge_rounds.is_complete = 1 THEN bridge_rounds.state ELSE 'Started' END,
                updated_utc = CASE WHEN bridge_rounds.updated_utc > excluded.updated_utc THEN bridge_rounds.updated_utc ELSE excluded.updated_utc END;
            """;
        AddRoundIdentityParameters(command, deskId, deviceId, shoe, round, roundId);
        command.Parameters.AddWithValue("$started_utc", startedUtc);
        command.Parameters.AddWithValue("$updated_utc", updatedUtc);
        command.Parameters.AddWithValue("$event_id", eventId);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task UpsertCardDrawnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deskId,
        string deviceId,
        long shoe,
        long round,
        long? roundId,
        string occurredUtc,
        long eventId,
        JsonElement data)
    {
        List<BridgeRoundCardProjection> cards = await ReadProjectedCardsAsync(connection, transaction, deskId, shoe, round).ConfigureAwait(false);
        string target = ReadJsonString(data, "target");
        int index = ReadJsonInt32(data, "index");
        string suit = ReadJsonString(data, "suit");
        string value = ReadJsonString(data, "value");
        if (target is not ("Player" or "Banker") ||
            index is < 1 or > 3 ||
            suit is not ("Diamond" or "Club" or "Spade" or "Heart") ||
            value is not ("A" or "2" or "3" or "4" or "5" or "6" or "7" or "8" or "9" or "10" or "J" or "Q" or "K"))
        {
            throw new InvalidDataException(
                $"Card projection input is invalid for {deskId} {shoe}/{round}.");
        }

        BridgeRoundCardProjection card = new(
            target,
            index,
            suit,
            value,
            eventId,
            occurredUtc);

        BridgeRoundCardProjection? existing = cards.FirstOrDefault(candidate =>
            string.Equals(candidate.Target, target, StringComparison.OrdinalIgnoreCase) &&
            candidate.Index == index);
        if (existing is not null)
        {
            if (string.Equals(existing.Suit, suit, StringComparison.Ordinal) &&
                string.Equals(existing.Value, value, StringComparison.Ordinal))
            {
                return;
            }

            throw new InvalidDataException(
                $"Conflicting card projection for {deskId} {shoe}/{round} {target} #{index}.");
        }

        cards.Add(card);
        cards.Sort(static (left, right) => left.EventId.CompareTo(right.EventId));
        string cardsJson = JsonSerializer.Serialize(cards);

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO bridge_rounds
                (desk_id, device_id, shoe, round, round_id, state, cards_json, updated_utc, is_complete)
            VALUES
                ($desk_id, $device_id, $shoe, $round, $round_id, 'Dealing', $cards_json, $updated_utc, 0)
            ON CONFLICT (desk_id, shoe, round) DO UPDATE SET
                device_id = excluded.device_id,
                round_id = COALESCE(bridge_rounds.round_id, excluded.round_id),
                cards_json = excluded.cards_json,
                state = CASE WHEN bridge_rounds.is_complete = 1 THEN bridge_rounds.state ELSE 'Dealing' END,
                updated_utc = CASE WHEN bridge_rounds.updated_utc > excluded.updated_utc THEN bridge_rounds.updated_utc ELSE excluded.updated_utc END;
            """;
        AddRoundIdentityParameters(command, deskId, deviceId, shoe, round, roundId);
        command.Parameters.AddWithValue("$cards_json", cardsJson);
        command.Parameters.AddWithValue("$updated_utc", occurredUtc);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task UpsertGameResultAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deskId,
        string deviceId,
        long shoe,
        long round,
        long? roundId,
        string occurredUtc,
        long eventId,
        string resultJson)
    {
        // A corrupt card projection must stop the terminal event transaction. Otherwise
        // an apparently normal GameResult could be queued from evidence that cannot be
        // trusted or queried after restart.
        _ = await ReadProjectedCardsAsync(
                connection,
                transaction,
                deskId,
                shoe,
                round)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO bridge_rounds
                (desk_id, device_id, shoe, round, round_id, settled_utc, state, result_json, result_event_id, updated_utc, is_complete)
            VALUES
                ($desk_id, $device_id, $shoe, $round, $round_id, $settled_utc, 'Settled', $result_json, $event_id, $updated_utc, 1)
            ON CONFLICT (desk_id, shoe, round) DO UPDATE SET
                device_id = excluded.device_id,
                round_id = COALESCE(bridge_rounds.round_id, excluded.round_id),
                settled_utc = excluded.settled_utc,
                state = 'Settled',
                result_json = excluded.result_json,
                result_event_id = excluded.result_event_id,
                updated_utc = CASE WHEN bridge_rounds.updated_utc > excluded.updated_utc THEN bridge_rounds.updated_utc ELSE excluded.updated_utc END,
                is_complete = 1;
            """;
        AddRoundIdentityParameters(command, deskId, deviceId, shoe, round, roundId);
        command.Parameters.AddWithValue("$settled_utc", occurredUtc);
        command.Parameters.AddWithValue("$result_json", resultJson);
        command.Parameters.AddWithValue("$updated_utc", occurredUtc);
        command.Parameters.AddWithValue("$event_id", eventId);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<List<BridgeRoundCardProjection>> ReadProjectedCardsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deskId,
        long shoe,
        long round)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT cards_json
            FROM bridge_rounds
            WHERE desk_id = $desk_id AND shoe = $shoe AND round = $round;
            """;
        command.Parameters.AddWithValue("$desk_id", deskId);
        command.Parameters.AddWithValue("$shoe", shoe);
        command.Parameters.AddWithValue("$round", round);
        object? stored = await command.ExecuteScalarAsync().ConfigureAwait(false);
        if (stored is not string json || string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            List<BridgeRoundCardProjection>? cards =
                JsonSerializer.Deserialize<List<BridgeRoundCardProjection>>(json);
            if (cards == null)
            {
                throw new InvalidDataException(
                    $"Persisted card projection is null for {deskId} {shoe}/{round}.");
            }

            ValidateProjectedCards(cards, deskId, shoe, round);
            return cards;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Persisted card projection is corrupt for {deskId} {shoe}/{round}.",
                ex);
        }
    }

    private static void ValidateProjectedCards(
        IReadOnlyList<BridgeRoundCardProjection> cards,
        string deskId,
        long shoe,
        long round)
    {
        if (cards.Count > 6)
        {
            throw new InvalidDataException(
                $"Persisted card projection exceeds baccarat limits for {deskId} {shoe}/{round}.");
        }

        HashSet<string> positions = new(StringComparer.OrdinalIgnoreCase);
        foreach (BridgeRoundCardProjection card in cards)
        {
            string target = card.Target?.Trim() ?? string.Empty;
            if (target is not ("Player" or "Banker") ||
                card.Index is < 1 or > 3 ||
                !positions.Add($"{target}:{card.Index}") ||
                string.IsNullOrWhiteSpace(card.Suit) ||
                string.IsNullOrWhiteSpace(card.Value) ||
                card.EventId <= 0 ||
                string.IsNullOrWhiteSpace(card.OccurredUtc) ||
                !DateTimeOffset.TryParse(
                    card.OccurredUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out _))
            {
                throw new InvalidDataException(
                    $"Persisted card projection is malformed or contradictory for {deskId} {shoe}/{round}.");
            }
        }
    }

    private static void AddRoundIdentityParameters(
        SqliteCommand command,
        string deskId,
        string deviceId,
        long shoe,
        long round,
        long? roundId)
    {
        command.Parameters.AddWithValue("$desk_id", deskId);
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$shoe", shoe);
        command.Parameters.AddWithValue("$round", round);
        command.Parameters.AddWithValue("$round_id", roundId.HasValue ? roundId.Value : DBNull.Value);
    }

    private static string ReadJsonString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out JsonElement property))
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() ?? string.Empty : property.ToString();
    }

    private static int ReadJsonInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out JsonElement property))
        {
            return 0;
        }

        return property.TryGetInt32(out int value) ? value : 0;
    }

    private static bool ReadJsonBoolean(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
            property.GetBoolean();
    }

    private static bool HasJsonProperty(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out _);

    private static string NormalizeUtcText(string text)
    {
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)
            : text.Trim();
    }

    /// <summary>
    /// Reads pending or retry-due events for API delivery.
    /// </summary>
    /// <param name="limit">Maximum number of events to return.</param>
    /// <param name="utcNow">Compatibility timestamp retained for existing callers; normal delivery is single-attempt.</param>
    /// <returns>Pending events ordered by event ID.</returns>
    public async Task<List<BridgePendingEvent>> GetDueOutboxEventsAsync(int limit, DateTime utcNow)
    {
        _ = utcNow;
        List<BridgePendingEvent> events = [];

        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.event_id, e.type, e.desk_id, e.device_id, e.shoe, e.round, e.payload_json, e.retry_count, e.event_uid
            FROM bridge_events e
            WHERE e.status = 'Pending'
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM bridge_events older
                  WHERE older.status = 'Pending'
                    AND older.desk_id = e.desk_id
                    AND older.device_id = e.device_id
                    AND older.event_id < e.event_id
              )
            ORDER BY e.event_id ASC
            LIMIT $limit;
            """;
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
                reader.GetInt32(7),
                reader.GetString(8)));
        }

        return events;
    }

    /// <summary>
    /// Returns whether the current round has a StartGame whose delivery is still
    /// eligible for one live GameResult attempt.
    /// </summary>
    public async Task<bool> HasDeliverableStartGameAsync(
        string sourceDataCode,
        string deviceId,
        long shoe,
        long round,
        long? roundId)
    {
        string? status = await ReadLatestStartGameStatusAsync(
                sourceDataCode,
                deviceId,
                shoe,
                round,
                roundId)
            .ConfigureAwait(false);
        return IsStartGameEligibleForResult(status, includePending: true);
    }

    /// <summary>
    /// Verifies the persisted StartGame state immediately before a GameResult is
    /// claimed for delivery. Definitively unregistered rounds are marked skipped.
    /// </summary>
    public async Task<bool> PrepareGameResultForDeliveryAsync(long eventId)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        string? startStatus;
        await using (SqliteCommand query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT start.status
                FROM bridge_events result
                LEFT JOIN bridge_events start
                  ON start.type = 'StartGame'
                 AND start.desk_id = result.desk_id
                 AND start.device_id = result.device_id
                 AND start.shoe = result.shoe
                 AND start.round = result.round
                 AND (start.round_id = result.round_id OR (start.round_id IS NULL AND result.round_id IS NULL))
                WHERE result.event_id = $event_id
                  AND result.type = 'GameResult'
                ORDER BY start.event_id DESC
                LIMIT 1;
                """;
            query.Parameters.AddWithValue("$event_id", eventId);
            object? scalar = await query.ExecuteScalarAsync().ConfigureAwait(false);
            startStatus = scalar is null or DBNull ? null : Convert.ToString(scalar, CultureInfo.InvariantCulture);
        }

        if (IsStartGameEligibleForResult(startStatus, includePending: false))
        {
            return true;
        }

        if (string.Equals(startStatus, "Pending", StringComparison.Ordinal))
        {
            return false;
        }

        await using SqliteCommand skip = connection.CreateCommand();
        skip.CommandText = """
            UPDATE bridge_events
            SET status = 'UnregisteredSkipped',
                next_retry_utc = NULL,
                last_error = $reason
            WHERE event_id = $event_id
              AND status = 'Pending';
            """;
        skip.Parameters.AddWithValue("$event_id", eventId);
        skip.Parameters.AddWithValue(
            "$reason",
            "GameResult was not delivered because its StartGame was not accepted or was not registered.");
        await skip.ExecuteNonQueryAsync().ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// CardDrawn 只用於目前牌局的即時畫面；必須等同局 StartGame 已明確送達，
    /// 否則保留本機且不跨局、跨重啟補送，避免牌面落到 BMS 的錯誤時間線。
    /// </summary>
    public async Task<bool> PrepareCardDrawnForDeliveryAsync(long eventId)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);

        string? startStatus;
        await using (SqliteCommand query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT start.status
                FROM bridge_events card
                LEFT JOIN bridge_events start
                  ON start.type = 'StartGame'
                 AND start.desk_id = card.desk_id
                 AND start.device_id = card.device_id
                 AND start.shoe = card.shoe
                 AND start.round = card.round
                 AND (start.round_id = card.round_id OR (start.round_id IS NULL AND card.round_id IS NULL))
                WHERE card.event_id = $event_id
                  AND card.type = 'CardDrawn'
                ORDER BY start.event_id DESC
                LIMIT 1;
                """;
            query.Parameters.AddWithValue("$event_id", eventId);
            object? scalar = await query.ExecuteScalarAsync().ConfigureAwait(false);
            startStatus = scalar is null or DBNull
                ? null
                : Convert.ToString(scalar, CultureInfo.InvariantCulture);
        }

        if (string.Equals(startStatus, "Sent", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(startStatus, "Pending", StringComparison.Ordinal))
        {
            return false;
        }

        await using SqliteCommand localOnly = connection.CreateCommand();
        localOnly.CommandText = """
            UPDATE bridge_events
            SET status = 'LocalOnly',
                next_retry_utc = NULL,
                last_error = $reason
            WHERE event_id = $event_id
              AND status = 'Pending';
            """;
        localOnly.Parameters.AddWithValue("$event_id", eventId);
        localOnly.Parameters.AddWithValue(
            "$reason",
            "CardDrawn was not delivered because its StartGame was not confirmed as sent.");
        await localOnly.ExecuteNonQueryAsync().ConfigureAwait(false);
        return false;
    }

    private async Task<string?> ReadLatestStartGameStatusAsync(
        string sourceDataCode,
        string deviceId,
        long shoe,
        long round,
        long? roundId)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT status
            FROM bridge_events
            WHERE type = 'StartGame'
              AND desk_id = $desk_id
              AND device_id = $device_id
              AND shoe = $shoe
              AND round = $round
              AND (round_id = $round_id OR (round_id IS NULL AND $round_id IS NULL))
            ORDER BY event_id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$desk_id", sourceDataCode);
        command.Parameters.AddWithValue("$device_id", deviceId);
        command.Parameters.AddWithValue("$shoe", shoe);
        command.Parameters.AddWithValue("$round", round);
        command.Parameters.AddWithValue("$round_id", roundId.HasValue ? roundId.Value : DBNull.Value);
        object? scalar = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return scalar is null or DBNull ? null : Convert.ToString(scalar, CultureInfo.InvariantCulture);
    }

    private static bool IsStartGameEligibleForResult(string? status, bool includePending)
    {
        return status is "Sent" or "Unconfirmed" or "Failed" ||
               (includePending && string.Equals(status, "Pending", StringComparison.Ordinal));
    }

    /// <summary>
    /// Atomically claims one pending event before any HTTP request is attempted.
    /// A crash after this point leaves the event Unconfirmed and never auto-replays it.
    /// </summary>
    public async Task<bool> TryClaimForDeliveryAsync(long eventId, DateTime utcNow)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE bridge_events
            SET status = 'Unconfirmed',
                last_attempt_utc = $now,
                next_retry_utc = NULL,
                last_error = NULL
            WHERE event_id = $event_id
              AND status = 'Pending';
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        command.Parameters.AddWithValue("$now", utcNow.ToString("o", CultureInfo.InvariantCulture));
        return await command.ExecuteNonQueryAsync().ConfigureAwait(false) == 1;
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
                SELECT IFNULL(SUM(CASE
                           WHEN status IN ('Pending', 'Failed', 'Unconfirmed', 'LegacyUnconfirmed', 'Rejected', 'UnregisteredSkipped') THEN 1
                           ELSE 0
                       END), 0),
                       IFNULL(SUM(CASE WHEN status IN ('Failed', 'Unconfirmed', 'LegacyUnconfirmed', 'Rejected', 'UnregisteredSkipped') THEN 1 ELSE 0 END), 0),
                       IFNULL(MAX(CASE
                           WHEN status IN ('Pending', 'Failed', 'Unconfirmed', 'LegacyUnconfirmed', 'Rejected', 'UnregisteredSkipped') THEN retry_count
                           ELSE 0
                       END), 0),
                       MIN(CASE WHEN status IN ('Failed', 'Unconfirmed', 'LegacyUnconfirmed', 'Rejected', 'UnregisteredSkipped') THEN last_attempt_utc ELSE NULL END)
                FROM bridge_events
                WHERE desk_id = $desk_id
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
                WHERE status IN ('Failed', 'Unconfirmed', 'LegacyUnconfirmed', 'Rejected', 'UnregisteredSkipped')
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
    /// <param name="httpStatus">HTTP status returned by BMS, when available.</param>
    public async Task MarkSentAsync(long eventId, DateTime utcNow, int? httpStatus = null)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE bridge_events
            SET status = 'Sent',
                sent_utc = $now,
                last_attempt_utc = $now,
                next_retry_utc = NULL,
                last_error = NULL
            WHERE event_id = $event_id
              AND status = 'Unconfirmed';
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        command.Parameters.AddWithValue("$now", utcNow.ToString("o", CultureInfo.InvariantCulture));
        if (await command.ExecuteNonQueryAsync().ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            return;
        }
        await InsertDeliveryAttemptAsync(
            connection,
            transaction,
            eventId,
            utcNow,
            succeeded: true,
            httpStatus,
            retryCount: null,
            nextRetryUtc: null,
            error: string.Empty).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Marks an outbox delivery attempt as failed without scheduling an automatic retry.
    /// </summary>
    /// <param name="eventId">Local event ID.</param>
    /// <param name="retryCount">Updated retry count.</param>
    /// <param name="utcNow">Failure time in UTC.</param>
    /// <param name="error">Failure detail to store.</param>
    /// <param name="httpStatus">HTTP status returned by BMS, when available.</param>
    public async Task MarkFailedAsync(long eventId, int retryCount, DateTime utcNow, string error, int? httpStatus = null)
        => await MarkAttemptFailureAsync(
                eventId,
                retryCount,
                utcNow,
                error,
                httpStatus,
                "Failed")
            .ConfigureAwait(false);

    /// <summary>Records an ACK-unknown attempt without scheduling an automatic replay.</summary>
    public async Task MarkUnconfirmedAsync(
        long eventId,
        int retryCount,
        DateTime utcNow,
        string error,
        int? httpStatus = null)
        => await MarkAttemptFailureAsync(
                eventId,
                retryCount,
                utcNow,
                error,
                httpStatus,
                "Unconfirmed")
            .ConfigureAwait(false);

    /// <summary>Records a definitive BMS or local validation rejection.</summary>
    public async Task MarkRejectedAsync(
        long eventId,
        int retryCount,
        DateTime utcNow,
        string error,
        int? httpStatus = null)
        => await MarkAttemptFailureAsync(
                eventId,
                retryCount,
                utcNow,
                error,
                httpStatus,
                "Rejected")
            .ConfigureAwait(false);

    private async Task MarkAttemptFailureAsync(
        long eventId,
        int retryCount,
        DateTime utcNow,
        string error,
        int? httpStatus,
        string status)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE bridge_events
            SET status = $status,
                retry_count = $retry_count,
                last_attempt_utc = $now,
                next_retry_utc = NULL,
                last_error = $last_error,
                reconciliation_state = CASE
                    WHEN $status = 'Unconfirmed' THEN 'Unconfirmed'
                    ELSE reconciliation_state
                END,
                reconciliation_decision_count = CASE
                    WHEN $status = 'Unconfirmed' THEN 0
                    ELSE reconciliation_decision_count
                END,
                next_reconcile_utc = CASE
                    WHEN $status = 'Unconfirmed' THEN $now
                    ELSE next_reconcile_utc
                END,
                recovery_command_id = CASE
                    WHEN $status = 'Unconfirmed' THEN NULL
                    ELSE recovery_command_id
                END,
                recovery_terminal_reason = CASE
                    WHEN $status = 'Unconfirmed' THEN NULL
                    ELSE recovery_terminal_reason
                END,
                unconfirmed_since_utc = CASE
                    WHEN $status = 'Unconfirmed' THEN COALESCE(unconfirmed_since_utc, $now)
                    ELSE unconfirmed_since_utc
                END
            WHERE event_id = $event_id
              AND status = 'Unconfirmed';
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$retry_count", retryCount);
        command.Parameters.AddWithValue("$now", utcNow.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$last_error", RedactForStore(error));
        if (await command.ExecuteNonQueryAsync().ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync().ConfigureAwait(false);
            return;
        }
        await InsertDeliveryAttemptAsync(
            connection,
            transaction,
            eventId,
            utcNow,
            succeeded: false,
            httpStatus,
            retryCount,
            nextRetryUtc: null,
            error).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a bounded batch of delivery-unknown identities that are due for
    /// control-plane reconciliation. Full result payloads are never returned.
    /// </summary>
    public async Task<List<BridgeRecoveryCandidate>> GetDueRecoveryCandidatesAsync(
        int limit,
        DateTimeOffset utcNow)
    {
        List<BridgeRecoveryCandidate> candidates = [];
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, event_uid, type, desk_id, device_id, shoe, round, round_id,
                   COALESCE(last_attempt_utc, occurred_utc), reconciliation_decision_count,
                   reconciliation_state
            FROM bridge_events
            WHERE status = 'Unconfirmed'
              AND reconciliation_state IN ('Unconfirmed', 'RecoveryUnconfirmed')
              AND (next_reconcile_utc IS NULL OR next_reconcile_utc <= $now)
              AND type IN ('StartGame', 'GameResult')
            ORDER BY COALESCE(next_reconcile_utc, last_attempt_utc, occurred_utc), event_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", utcNow.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 20));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            candidates.Add(new BridgeRecoveryCandidate(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7),
                ParseUtcOffset(reader.GetString(8)),
                reader.GetInt32(9),
                reader.GetString(10)));
        }

        return candidates;
    }

    /// <summary>
    /// Applies one explicit BMS reconciliation decision without changing the
    /// original one-shot delivery status.
    /// </summary>
    public async Task<bool> ApplyRecoveryDecisionAsync(
        BridgeRecoveryCandidate candidate,
        string decision,
        string commandId,
        string message,
        DateTimeOffset observedAt)
    {
        string state = decision switch
        {
            "RecoverRound" => "RecoveryRequested",
            "AwaitingOperator" => "AwaitingOperator",
            "AlreadyAccepted" => "AlreadyAccepted",
            "NotRegistered" => "NotRegistered",
            "Recovered" => "Recovered",
            "NotFound" => "NotFound",
            "Conflict" => "Conflict",
            "Cancelled" => "Cancelled",
            "Expired" => "Expired",
            "ManualReview" => "ManualReview",
            _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "Unsupported recovery decision.")
        };
        bool terminal = state is not ("Unconfirmed" or "RecoveryRequested");
        string normalizedCommandId = state == "RecoveryRequested" ? commandId.Trim() : string.Empty;
        string terminalReason = terminal ? RedactForStore(message) : string.Empty;

        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE bridge_events
            SET reconciliation_state = $state,
                reconciliation_decision_count = reconciliation_decision_count + 1,
                next_reconcile_utc = NULL,
                recovery_command_id = CASE
                    WHEN $command_id = '' THEN recovery_command_id
                    ELSE $command_id
                END,
                recovery_terminal_reason = CASE
                    WHEN $terminal = 1 THEN $reason
                    ELSE NULL
                END
            WHERE event_id = $event_id
              AND event_uid = $event_uid
              AND status = 'Unconfirmed'
              AND reconciliation_state IN ('Unconfirmed', 'RecoveryUnconfirmed');
            """;
        update.Parameters.AddWithValue("$state", state);
        update.Parameters.AddWithValue("$command_id", normalizedCommandId);
        update.Parameters.AddWithValue("$terminal", terminal ? 1 : 0);
        update.Parameters.AddWithValue(
            "$reason",
            string.IsNullOrWhiteSpace(terminalReason) ? DBNull.Value : terminalReason);
        update.Parameters.AddWithValue("$event_id", candidate.EventId);
        update.Parameters.AddWithValue("$event_uid", candidate.EventUid);
        bool changed = await update.ExecuteNonQueryAsync().ConfigureAwait(false) == 1;
        await transaction.CommitAsync().ConfigureAwait(false);
        _ = observedAt;
        return changed;
    }

    /// <summary>
    /// Moves an exact retained event into the command-authorized recovery state.
    /// This also supports commands that arrive after an AwaitingOperator decision
    /// removed the event from per-event reconciliation.
    /// </summary>
    public async Task<bool> MarkRecoveryRequestedAsync(
        long eventId,
        string eventUid,
        string commandId)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE bridge_events
            SET reconciliation_state = 'RecoveryRequested',
                next_reconcile_utc = NULL,
                recovery_command_id = $command_id,
                recovery_terminal_reason = NULL
            WHERE event_id = $event_id
              AND event_uid = $event_uid COLLATE NOCASE
              AND status = 'Unconfirmed'
              AND reconciliation_state IN
                  ('Unconfirmed', 'AwaitingOperator', 'RecoveryUnconfirmed', 'RecoveryRequested',
                   'NotFound', 'Conflict', 'Cancelled', 'Expired', 'ManualReview')
              AND (reconciliation_state <> 'RecoveryRequested' OR recovery_command_id = $command_id)
              AND EXISTS
              (
                  SELECT 1
                  FROM bridge_recovery_requests AS active
                  WHERE active.command_id = $command_id
                    AND active.command_type = 'RecoverRound'
                    AND active.event_id = $event_id
                    AND active.event_uid = $event_uid COLLATE NOCASE
                    AND active.result = 'RecoveryRequested'
              )
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM bridge_recovery_requests AS competing
                  WHERE competing.command_type = 'RecoverRound'
                    AND competing.event_id = $event_id
                    AND competing.event_uid = $event_uid COLLATE NOCASE
                    AND competing.result IN ('RecoveryRequested', 'RecoveryUnconfirmed')
                    AND competing.command_id <> $command_id
              );
            """;
        update.Parameters.AddWithValue("$event_id", eventId);
        update.Parameters.AddWithValue("$event_uid", eventUid);
        update.Parameters.AddWithValue("$command_id", commandId);
        bool changed = await update.ExecuteNonQueryAsync().ConfigureAwait(false) == 1;
        await transaction.CommitAsync().ConfigureAwait(false);
        return changed;
    }

    /// <summary>
    /// Records that a submitted identity received no explicit decision and
    /// schedules bounded reconciliation backoff.
    /// </summary>
    public async Task RecordMissingRecoveryDecisionAsync(
        BridgeRecoveryCandidate candidate,
        DateTimeOffset observedAt)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();

        int currentCount;
        DateTimeOffset unconfirmedSince;
        await using (SqliteCommand query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT reconciliation_decision_count,
                       COALESCE(unconfirmed_since_utc, last_attempt_utc, occurred_utc)
                FROM bridge_events
                WHERE event_id = $event_id
                  AND event_uid = $event_uid
                  AND status = 'Unconfirmed'
                  AND reconciliation_state IN ('Unconfirmed', 'RecoveryUnconfirmed');
                """;
            query.Parameters.AddWithValue("$event_id", candidate.EventId);
            query.Parameters.AddWithValue("$event_uid", candidate.EventUid);
            await using SqliteDataReader reader = await query.ExecuteReaderAsync().ConfigureAwait(false);
            if (!await reader.ReadAsync().ConfigureAwait(false))
            {
                await transaction.RollbackAsync().ConfigureAwait(false);
                return;
            }

            currentCount = reader.GetInt32(0);
            unconfirmedSince = ParseUtcOffset(reader.GetString(1));
        }

        int nextCount = currentCount + 1;
        bool manualReview = nextCount >= 20 || observedAt - unconfirmedSince >= TimeSpan.FromHours(24);
        DateTimeOffset? nextAt = manualReview
            ? null
            : observedAt.Add(CalculateReconciliationBackoff(nextCount));
        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE bridge_events
            SET reconciliation_state = $state,
                reconciliation_decision_count = $decision_count,
                next_reconcile_utc = $next_reconcile_utc,
                recovery_terminal_reason = $terminal_reason
            WHERE event_id = $event_id
              AND event_uid = $event_uid
              AND status = 'Unconfirmed'
              AND reconciliation_state IN ('Unconfirmed', 'RecoveryUnconfirmed');
            """;
        update.Parameters.AddWithValue(
            "$state",
            manualReview ? "ManualReview" : candidate.ReconciliationState);
        update.Parameters.AddWithValue("$decision_count", nextCount);
        update.Parameters.AddWithValue(
            "$next_reconcile_utc",
            nextAt.HasValue
                ? nextAt.Value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)
                : DBNull.Value);
        update.Parameters.AddWithValue(
            "$terminal_reason",
            manualReview
                ? "No explicit BMS recovery decision was received within the reconciliation limit."
                : DBNull.Value);
        update.Parameters.AddWithValue("$event_id", candidate.EventId);
        update.Parameters.AddWithValue("$event_uid", candidate.EventUid);
        await update.ExecuteNonQueryAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Locates the exact retained GameResult authorized by a recovery command.
    /// A partial identity match is reported as a conflict instead of falling
    /// back to a broader round search.
    /// </summary>
    public async Task<BridgeRecoveryLookupResult> LookupRecoveryGameResultAsync(
        long eventId,
        string eventUid,
        string sourceDataCode,
        string deviceId,
        long shoe,
        long round,
        long? roundId)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id, event_uid, type, desk_id, device_id, shoe, round, round_id,
                   payload_json, retry_count
            FROM bridge_events
            WHERE event_id = $event_id OR event_uid = $event_uid
            ORDER BY CASE WHEN event_id = $event_id THEN 0 ELSE 1 END
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        command.Parameters.AddWithValue("$event_uid", eventUid);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            return BridgeRecoveryLookupResult.NotFound();
        }

        long storedEventId = reader.GetInt64(0);
        string storedEventUid = reader.GetString(1);
        string type = reader.GetString(2);
        string storedDesk = reader.GetString(3);
        string storedDevice = reader.GetString(4);
        long storedShoe = reader.GetInt64(5);
        long storedRound = reader.GetInt64(6);
        long? storedRoundId = reader.IsDBNull(7) ? null : reader.GetInt64(7);
        bool exact =
            storedEventId == eventId &&
            string.Equals(storedEventUid, eventUid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(type, "GameResult", StringComparison.Ordinal) &&
            string.Equals(storedDesk, sourceDataCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(storedDevice, deviceId, StringComparison.OrdinalIgnoreCase) &&
            storedShoe == shoe &&
            storedRound == round &&
            storedRoundId == roundId;
        if (!exact)
        {
            return BridgeRecoveryLookupResult.Conflict(
                "Recovery command identity does not match the retained GameResult.");
        }

        return BridgeRecoveryLookupResult.Found(new BridgePendingEvent(
            storedEventId,
            type,
            storedDesk,
            storedDevice,
            storedShoe,
            storedRound,
            reader.GetString(8),
            reader.GetInt32(9),
            storedEventUid));
    }

    /// <summary>
    /// Atomically reserves one authoritative dispatch of a recovery command.
    /// Repeated polls with the same dispatch count cannot cause another POST;
    /// a higher BMS dispatch count may explicitly reauthorize a previously
    /// ACK-unknown command.
    /// </summary>
    public async Task<bool> TryBeginRecoveryCommandAsync(BridgeRecoveryAudit audit) =>
        (await ReserveRecoveryCommandAsync(audit).ConfigureAwait(false)).Disposition ==
        BridgeRecoveryReservationDisposition.Authorized;

    /// <summary>
    /// Atomically reserves one active recovery command generation for an exact
    /// event. A process crash after reservation can only be recovered by the
    /// same command and generation with a strictly higher dispatch count.
    /// </summary>
    public async Task<BridgeRecoveryReservationResult> ReserveRecoveryCommandAsync(
        BridgeRecoveryAudit audit)
    {
        if (!IsValidRecoveryReservation(audit))
        {
            return BridgeRecoveryReservationResult.Conflict(
                "Recovery reservation requires an exact event identity, generation, and dispatch count.");
        }

        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        var rows = new List<RecoveryCommandLedgerRow>();
        await using (SqliteCommand query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT command_id, command_type, result, generation, dispatch_count,
                       event_id, event_uid, desk_id, device_id, shoe, round, round_id
                FROM bridge_recovery_requests
                WHERE command_id = $command_id
                   OR
                   (
                       command_type = 'RecoverRound'
                       AND
                       (
                           event_id = $event_id
                           OR event_uid = $event_uid COLLATE NOCASE
                       )
                   )
                ORDER BY generation, dispatch_count, command_id;
                """;
            query.Parameters.AddWithValue("$command_id", audit.CommandId);
            query.Parameters.AddWithValue("$event_id", audit.EventId!.Value);
            query.Parameters.AddWithValue("$event_uid", audit.EventUid);
            await using SqliteDataReader reader = await query.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                rows.Add(new RecoveryCommandLedgerRow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    reader.IsDBNull(10) ? null : reader.GetInt64(10),
                    reader.IsDBNull(11) ? null : reader.GetInt64(11)));
            }
        }

        RecoveryCommandLedgerRow? sameCommand = rows.SingleOrDefault(row =>
            string.Equals(row.CommandId, audit.CommandId, StringComparison.Ordinal));
        if (sameCommand != null)
        {
            if (!string.Equals(sameCommand.CommandType, "RecoverRound", StringComparison.Ordinal) ||
                !RecoveryIdentityMatches(sameCommand, audit) ||
                sameCommand.Generation != audit.Generation)
            {
                await transaction.CommitAsync().ConfigureAwait(false);
                return BridgeRecoveryReservationResult.Conflict(
                    "Recovery commandId is already associated with a different command generation or event identity.",
                    commandIdAlreadyExists: true);
            }

            if (IsActiveRecoveryResult(sameCommand.Result))
            {
                if (audit.DispatchCount <= sameCommand.DispatchCount)
                {
                    await transaction.CommitAsync().ConfigureAwait(false);
                    return BridgeRecoveryReservationResult.Duplicate(
                        "This recovery command dispatch was already reserved.");
                }

                await using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE bridge_recovery_requests
                    SET result = 'RecoveryRequested',
                        last_observed_utc = $last_observed_utc,
                        message = $message,
                        outcome = NULL,
                        next_retry_utc = NULL,
                        terminal_reason = NULL,
                        decision_count = MAX(decision_count, $decision_count),
                        dispatch_count = $dispatch_count
                    WHERE command_id = $command_id
                      AND command_type = 'RecoverRound'
                      AND result IN ('RecoveryRequested', 'RecoveryUnconfirmed')
                      AND generation = $generation
                      AND dispatch_count < $dispatch_count;
                    """;
                AddRecoveryAuditParameters(update, audit);
                bool changed = await update.ExecuteNonQueryAsync().ConfigureAwait(false) == 1;
                await transaction.CommitAsync().ConfigureAwait(false);
                return changed
                    ? BridgeRecoveryReservationResult.Authorized(
                        "A higher dispatch count explicitly reauthorized this recovery command.")
                    : BridgeRecoveryReservationResult.Conflict(
                        "Recovery command reservation changed concurrently.",
                        commandIdAlreadyExists: true);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
            return IsTerminalRecoveryResult(sameCommand.Result)
                ? BridgeRecoveryReservationResult.Duplicate(
                    "This recovery command already has a terminal outcome.")
                : BridgeRecoveryReservationResult.Conflict(
                    "Recovery command is not in a reservable state.");
        }

        RecoveryCommandLedgerRow[] eventRows = rows
            .Where(row => string.Equals(row.CommandType, "RecoverRound", StringComparison.Ordinal))
            .ToArray();
        if (eventRows.Any(row => !RecoveryIdentityMatches(row, audit)))
        {
            await transaction.CommitAsync().ConfigureAwait(false);
            return BridgeRecoveryReservationResult.Conflict(
                "Recovery eventId or eventUid is already associated with a different retained event identity.");
        }

        if (eventRows.Any(row => IsActiveRecoveryResult(row.Result)))
        {
            await transaction.CommitAsync().ConfigureAwait(false);
            return BridgeRecoveryReservationResult.Conflict(
                "Another recovery command generation is already active for this exact event.");
        }

        if (eventRows.Length == 0)
        {
            if (audit.Generation != 1)
            {
                await transaction.CommitAsync().ConfigureAwait(false);
                return BridgeRecoveryReservationResult.Conflict(
                    "The first recovery command for an event must use generation 1.");
            }

            bool inserted = await InsertRecoveryReservationAsync(
                    connection,
                    transaction,
                    audit)
                .ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
            return inserted
                ? BridgeRecoveryReservationResult.Authorized(
                    "Recovery command generation was reserved.")
                : BridgeRecoveryReservationResult.Conflict(
                    "Recovery command generation could not be reserved.");
        }

        int latestGeneration = eventRows.Max(static row => row.Generation);
        RecoveryCommandLedgerRow[] latestRows = eventRows
            .Where(row => row.Generation == latestGeneration)
            .ToArray();
        if (latestRows.Length != 1 ||
            !IsReopenableRecoveryResult(latestRows[0].Result) ||
            audit.Generation != latestGeneration + 1)
        {
            await transaction.CommitAsync().ConfigureAwait(false);
            return BridgeRecoveryReservationResult.Conflict(
                "A new recovery command requires one reopenable terminal predecessor and the next generation number.");
        }

        bool nextGenerationInserted = await InsertRecoveryReservationAsync(
                connection,
                transaction,
                audit)
            .ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        return nextGenerationInserted
            ? BridgeRecoveryReservationResult.Authorized(
                "A new authorized recovery command generation was reserved.")
            : BridgeRecoveryReservationResult.Conflict(
                "Recovery command generation could not be reserved.");
    }

    private static async Task<bool> InsertRecoveryReservationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BridgeRecoveryAudit audit)
    {
        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO bridge_recovery_requests
                (command_id, command_type, desk_id, device_id, shoe, round, round_id,
                 received_utc, last_observed_utc, result, next_retry_utc, message,
                 event_id, event_uid, outcome, decision_count, terminal_reason,
                 generation, dispatch_count)
            VALUES
                ($command_id, 'RecoverRound', $desk_id, $device_id, $shoe, $round, $round_id,
                 $received_utc, $last_observed_utc, 'RecoveryRequested', NULL, $message,
                 $event_id, $event_uid, NULL, $decision_count, NULL,
                 $generation, $dispatch_count);
            """;
        AddRecoveryAuditParameters(insert, audit);
        return await insert.ExecuteNonQueryAsync().ConfigureAwait(false) == 1;
    }

    private static bool IsValidRecoveryReservation(BridgeRecoveryAudit audit) =>
        !string.IsNullOrWhiteSpace(audit.CommandId) &&
        string.Equals(audit.CommandType, "RecoverRound", StringComparison.Ordinal) &&
        audit.EventId is > 0 &&
        Guid.TryParse(audit.EventUid, out Guid eventUid) &&
        eventUid != Guid.Empty &&
        !string.IsNullOrWhiteSpace(audit.SourceDataCode) &&
        !string.IsNullOrWhiteSpace(audit.DeviceId) &&
        audit.Shoe is > 0 &&
        audit.Round is > 0 &&
        audit.RoundId is > 0 &&
        audit.Generation > 0 &&
        audit.DispatchCount > 0;

    private static bool RecoveryIdentityMatches(
        RecoveryCommandLedgerRow row,
        BridgeRecoveryAudit audit) =>
        row.EventId == audit.EventId &&
        string.Equals(row.EventUid, audit.EventUid, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(row.SourceDataCode, audit.SourceDataCode, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(row.DeviceId, audit.DeviceId, StringComparison.OrdinalIgnoreCase) &&
        row.Shoe == audit.Shoe &&
        row.Round == audit.Round &&
        row.RoundId == audit.RoundId;

    /// <summary>
    /// Verifies that a terminal decision names the newest locally observed
    /// recovery command tuple for the exact retained event identity. Dispatch
    /// advancement must first be recorded by the command reservation path; a
    /// decision may not invent a newer dispatch count on its own.
    /// </summary>
    public async Task<bool> IsExactLatestRecoveryCommandAsync(
        BridgeRecoveryCandidate candidate,
        string commandId,
        int generation,
        int dispatchCount)
    {
        if (string.IsNullOrWhiteSpace(commandId) ||
            generation <= 0 ||
            dispatchCount <= 0)
        {
            return false;
        }

        var matches = new List<(string CommandId, int Generation, int DispatchCount)>();
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT command_id, generation, dispatch_count
            FROM bridge_recovery_requests
            WHERE command_type = 'RecoverRound'
              AND event_id = $event_id
              AND event_uid = $event_uid COLLATE NOCASE
              AND desk_id = $desk_id COLLATE NOCASE
              AND device_id = $device_id COLLATE NOCASE
              AND shoe = $shoe
              AND round = $round
              AND round_id = $round_id
            ORDER BY generation DESC, dispatch_count DESC, command_id;
            """;
        command.Parameters.AddWithValue("$event_id", candidate.EventId);
        command.Parameters.AddWithValue("$event_uid", candidate.EventUid);
        command.Parameters.AddWithValue("$desk_id", candidate.SourceDataCode);
        command.Parameters.AddWithValue("$device_id", candidate.DeviceId);
        command.Parameters.AddWithValue("$shoe", candidate.Shoe);
        command.Parameters.AddWithValue("$round", candidate.Round);
        command.Parameters.AddWithValue(
            "$round_id",
            candidate.RoundId.HasValue ? candidate.RoundId.Value : DBNull.Value);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            matches.Add((
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2)));
        }

        if (matches.Count == 0)
        {
            return false;
        }

        int latestGeneration = matches.Max(static match => match.Generation);
        (string CommandId, int Generation, int DispatchCount)[] latest = matches
            .Where(match => match.Generation == latestGeneration)
            .ToArray();
        if (latest.Length != 1)
        {
            return false;
        }

        (string storedCommandId, int storedGeneration, int storedDispatchCount) = latest[0];
        return generation == storedGeneration &&
               string.Equals(commandId, storedCommandId, StringComparison.Ordinal) &&
               dispatchCount == storedDispatchCount;
    }

    /// <summary>
    /// Persists the outcome of one dedicated recovery POST without mutating the
    /// original event delivery status.
    /// </summary>
    public async Task MarkRecoverySubmissionOutcomeAsync(
        long eventId,
        string eventUid,
        string commandId,
        string outcome,
        string message,
        DateTimeOffset observedAt)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE bridge_events
            SET reconciliation_state = $outcome,
                next_reconcile_utc = $next_reconcile_utc,
                recovery_command_id = $command_id,
                recovery_terminal_reason = $reason
            WHERE event_id = $event_id
              AND event_uid = $event_uid
              AND status = 'Unconfirmed'
              AND reconciliation_state = 'RecoveryRequested'
              AND recovery_command_id = $command_id;
            """;
        update.Parameters.AddWithValue("$outcome", outcome);
        update.Parameters.AddWithValue(
            "$next_reconcile_utc",
            string.Equals(outcome, "RecoveryUnconfirmed", StringComparison.Ordinal)
                ? observedAt.AddSeconds(30).UtcDateTime.ToString("o", CultureInfo.InvariantCulture)
                : DBNull.Value);
        update.Parameters.AddWithValue("$command_id", commandId);
        update.Parameters.AddWithValue("$reason", RedactForStore(message));
        update.Parameters.AddWithValue("$event_id", eventId);
        update.Parameters.AddWithValue("$event_uid", eventUid);
        await update.ExecuteNonQueryAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static TimeSpan CalculateReconciliationBackoff(int decisionCount)
    {
        int seconds = decisionCount switch
        {
            <= 1 => 30,
            2 => 60,
            3 => 120,
            4 => 300,
            5 => 600,
            _ => 1800
        };
        return TimeSpan.FromSeconds(seconds);
    }

    private static async Task InsertDeliveryAttemptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long eventId,
        DateTime attemptedUtc,
        bool succeeded,
        int? httpStatus,
        int? retryCount,
        DateTime? nextRetryUtc,
        string error)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO bridge_delivery_attempts
                (event_id, attempted_utc, succeeded, http_status, retry_count, next_retry_utc, error)
            SELECT event_id, $attempted_utc, $succeeded, $http_status,
                   COALESCE($retry_count, retry_count), $next_retry_utc, $error
            FROM bridge_events
            WHERE event_id = $event_id;
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        command.Parameters.AddWithValue("$attempted_utc", attemptedUtc.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$succeeded", succeeded ? 1 : 0);
        command.Parameters.AddWithValue("$http_status", httpStatus.HasValue ? httpStatus.Value : DBNull.Value);
        command.Parameters.AddWithValue("$retry_count", retryCount.HasValue ? retryCount.Value : DBNull.Value);
        command.Parameters.AddWithValue(
            "$next_retry_utc",
            nextRetryUtc.HasValue ? nextRetryUtc.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        string redactedError = RedactForStore(error);
        command.Parameters.AddWithValue("$error", string.IsNullOrWhiteSpace(redactedError) ? DBNull.Value : redactedError);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    /// <summary>Persists the latest observed decision for a BMS recovery or resend command.</summary>
    public async Task RecordRecoveryRequestAsync(BridgeRecoveryAudit audit)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        string? currentResult = null;
        int currentGeneration = 0;
        int currentDispatchCount = 0;
        await using (SqliteCommand query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT result, generation, dispatch_count
                FROM bridge_recovery_requests
                WHERE command_id = $command_id;
                """;
            query.Parameters.AddWithValue("$command_id", audit.CommandId);
            await using SqliteDataReader reader = await query
                .ExecuteReaderAsync()
                .ConfigureAwait(false);
            if (await reader.ReadAsync().ConfigureAwait(false))
            {
                currentResult = reader.GetString(0);
                currentGeneration = reader.GetInt32(1);
                currentDispatchCount = reader.GetInt32(2);
            }
        }

        if (currentResult == null)
        {
            await using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO bridge_recovery_requests
                    (command_id, command_type, desk_id, device_id, shoe, round, round_id,
                     received_utc, last_observed_utc, result, next_retry_utc, message,
                     event_id, event_uid, outcome, decision_count, terminal_reason,
                     generation, dispatch_count)
                VALUES
                    ($command_id, $command_type, $desk_id, $device_id, $shoe, $round, $round_id,
                     $received_utc, $last_observed_utc, $result, $next_retry_utc, $message,
                     $event_id, $event_uid, $outcome, $decision_count, $terminal_reason,
                     $generation, $dispatch_count);
                """;
            AddRecoveryAuditParameters(insert, audit);
            await insert.ExecuteNonQueryAsync().ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
            return;
        }

        bool exactAuthorizationTuple =
            currentGeneration == Math.Max(0, audit.Generation) &&
            currentDispatchCount == Math.Max(0, audit.DispatchCount);
        bool replaceOutcome =
            exactAuthorizationTuple &&
            CanAdvanceRecoveryResult(currentResult, audit.Result);
        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE bridge_recovery_requests
            SET last_observed_utc = $last_observed_utc,
                result = CASE WHEN $replace_outcome = 1 THEN $result ELSE result END,
                next_retry_utc = CASE WHEN $replace_outcome = 1 THEN $next_retry_utc ELSE next_retry_utc END,
                message = CASE WHEN $replace_outcome = 1 THEN $message ELSE message END,
                event_id = COALESCE(event_id, $event_id),
                event_uid = CASE
                    WHEN event_uid IS NULL OR trim(event_uid) = '' THEN $event_uid
                    ELSE event_uid
                END,
                outcome = CASE WHEN $replace_outcome = 1 THEN $outcome ELSE outcome END,
                decision_count = MAX(decision_count, $decision_count),
                terminal_reason = CASE
                    WHEN $replace_outcome = 1 THEN $terminal_reason
                    ELSE terminal_reason
                END
            WHERE command_id = $command_id;
            """;
        AddRecoveryAuditParameters(update, audit);
        update.Parameters.AddWithValue("$replace_outcome", replaceOutcome ? 1 : 0);
        await update.ExecuteNonQueryAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static void AddRecoveryAuditParameters(SqliteCommand command, BridgeRecoveryAudit audit)
    {
        command.Parameters.AddWithValue("$command_id", audit.CommandId);
        command.Parameters.AddWithValue("$command_type", audit.CommandType);
        command.Parameters.AddWithValue("$desk_id", audit.SourceDataCode);
        command.Parameters.AddWithValue("$device_id", audit.DeviceId);
        command.Parameters.AddWithValue("$shoe", audit.Shoe.HasValue ? audit.Shoe.Value : DBNull.Value);
        command.Parameters.AddWithValue("$round", audit.Round.HasValue ? audit.Round.Value : DBNull.Value);
        command.Parameters.AddWithValue("$round_id", audit.RoundId.HasValue ? audit.RoundId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$received_utc", audit.ReceivedUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$last_observed_utc", audit.ObservedUtc.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$result", audit.Result);
        command.Parameters.AddWithValue(
            "$next_retry_utc",
            audit.NextRetryUtc.HasValue && !IsTerminalRecoveryResult(audit.Result)
                ? audit.NextRetryUtc.Value.UtcDateTime.ToString("o", CultureInfo.InvariantCulture)
                : DBNull.Value);
        string message = RedactForStore(audit.Message);
        command.Parameters.AddWithValue("$message", string.IsNullOrWhiteSpace(message) ? DBNull.Value : message);
        command.Parameters.AddWithValue("$event_id", audit.EventId.HasValue ? audit.EventId.Value : DBNull.Value);
        command.Parameters.AddWithValue(
            "$event_uid",
            string.IsNullOrWhiteSpace(audit.EventUid) ? DBNull.Value : audit.EventUid);
        command.Parameters.AddWithValue(
            "$outcome",
            string.IsNullOrWhiteSpace(audit.Outcome) ? DBNull.Value : audit.Outcome);
        command.Parameters.AddWithValue("$decision_count", Math.Max(0, audit.DecisionCount));
        string terminalReason = RedactForStore(audit.TerminalReason);
        command.Parameters.AddWithValue(
            "$terminal_reason",
            string.IsNullOrWhiteSpace(terminalReason) ? DBNull.Value : terminalReason);
        command.Parameters.AddWithValue("$generation", Math.Max(0, audit.Generation));
        command.Parameters.AddWithValue("$dispatch_count", Math.Max(0, audit.DispatchCount));
    }

    private static bool CanAdvanceRecoveryResult(string current, string next)
    {
        if (string.Equals(current, next, StringComparison.Ordinal))
        {
            return true;
        }

        if (current == "RecoveryUnconfirmed" &&
            next is "Recovered" or "AlreadyAccepted")
        {
            return true;
        }

        return !IsTerminalRecoveryResult(current);
    }

    private static bool IsTerminalRecoveryResult(string result) =>
        result is "Recovered" or "AlreadyAccepted" or "NotFound" or "Conflict" or
            "Rejected" or "Cancelled" or "Expired" or "ManualReview";

    private static bool IsActiveRecoveryResult(string result) =>
        result is "RecoveryRequested" or "RecoveryUnconfirmed";

    private static bool IsReopenableRecoveryResult(string result) =>
        result is "NotFound" or "Conflict" or "Cancelled" or "Expired" or "ManualReview";

    private sealed record RecoveryCommandLedgerRow(
        string CommandId,
        string CommandType,
        string Result,
        int Generation,
        int DispatchCount,
        long? EventId,
        string? EventUid,
        string SourceDataCode,
        string DeviceId,
        long? Shoe,
        long? Round,
        long? RoundId);

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
        VerifyIntegrity(connection);
        ExecuteNonQuery(connection, "PRAGMA journal_mode=WAL;");
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS bridge_events
            (
                event_id INTEGER PRIMARY KEY,
                event_uid TEXT NOT NULL,
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
        EnsureColumn(connection, "bridge_events", "event_uid", "event_uid TEXT NULL");
        EnsureColumn(connection, "bridge_events", "status", "status TEXT NOT NULL DEFAULT 'Pending'");
        EnsureColumn(connection, "bridge_events", "retry_count", "retry_count INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "bridge_events", "next_retry_utc", "next_retry_utc TEXT NULL");
        EnsureColumn(connection, "bridge_events", "last_attempt_utc", "last_attempt_utc TEXT NULL");
        EnsureColumn(connection, "bridge_events", "sent_utc", "sent_utc TEXT NULL");
        EnsureColumn(connection, "bridge_events", "last_error", "last_error TEXT NULL");
        EnsureColumn(connection, "bridge_events", "reconciliation_state", "reconciliation_state TEXT NULL");
        EnsureColumn(connection, "bridge_events", "reconciliation_decision_count", "reconciliation_decision_count INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "bridge_events", "next_reconcile_utc", "next_reconcile_utc TEXT NULL");
        EnsureColumn(connection, "bridge_events", "recovery_command_id", "recovery_command_id TEXT NULL");
        EnsureColumn(connection, "bridge_events", "recovery_terminal_reason", "recovery_terminal_reason TEXT NULL");
        EnsureColumn(connection, "bridge_events", "unconfirmed_since_utc", "unconfirmed_since_utc TEXT NULL");
        MigrateEventUids(connection);
        MigrateDiagnosticEventsToLocalOnly(connection);
        QuarantinePendingEventsFromPreviousRun(connection);
        InitializeRecoveryReconciliation(connection);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS bridge_rounds
            (
                desk_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                shoe INTEGER NOT NULL,
                round INTEGER NOT NULL,
                round_id INTEGER NULL,
                started_utc TEXT NULL,
                settled_utc TEXT NULL,
                state TEXT NOT NULL DEFAULT 'Incomplete',
                cards_json TEXT NULL,
                result_json TEXT NULL,
                start_event_id INTEGER NULL,
                result_event_id INTEGER NULL,
                updated_utc TEXT NOT NULL,
                is_complete INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (desk_id, shoe, round)
            );
            """);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS bridge_delivery_attempts
            (
                attempt_id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id INTEGER NOT NULL,
                attempted_utc TEXT NOT NULL,
                succeeded INTEGER NOT NULL,
                http_status INTEGER NULL,
                retry_count INTEGER NOT NULL,
                next_retry_utc TEXT NULL,
                error TEXT NULL
            );
            """);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS bridge_recovery_requests
            (
                command_id TEXT PRIMARY KEY,
                command_type TEXT NOT NULL,
                desk_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                shoe INTEGER NULL,
                round INTEGER NULL,
                round_id INTEGER NULL,
                received_utc TEXT NOT NULL,
                last_observed_utc TEXT NOT NULL,
                result TEXT NOT NULL,
                next_retry_utc TEXT NULL,
                message TEXT NULL
            );
            """);
        ExecuteNonQuery(connection, """
            CREATE TABLE IF NOT EXISTS bridge_raw_frames
            (
                raw_frame_id INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_utc TEXT NOT NULL,
                desk_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                shoe INTEGER NOT NULL,
                round INTEGER NOT NULL,
                round_id INTEGER NULL,
                direction TEXT NOT NULL,
                raw_hex TEXT NOT NULL
            );
            """);
        EnsureColumn(connection, "bridge_recovery_requests", "event_id", "event_id INTEGER NULL");
        EnsureColumn(connection, "bridge_recovery_requests", "event_uid", "event_uid TEXT NULL");
        EnsureColumn(connection, "bridge_recovery_requests", "outcome", "outcome TEXT NULL");
        EnsureColumn(connection, "bridge_recovery_requests", "decision_count", "decision_count INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "bridge_recovery_requests", "terminal_reason", "terminal_reason TEXT NULL");
        EnsureColumn(connection, "bridge_recovery_requests", "generation", "generation INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "bridge_recovery_requests", "dispatch_count", "dispatch_count INTEGER NOT NULL DEFAULT 0");
        QuarantineInvalidOrDuplicateActiveRecoveryCommands(connection);
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
        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS ix_bridge_rounds_query
                ON bridge_rounds (desk_id, updated_utc DESC, shoe DESC, round DESC);
            """);
        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS ix_bridge_rounds_state
                ON bridge_rounds (state, updated_utc DESC);
            """);
        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS ix_bridge_raw_frames_round
                ON bridge_raw_frames (desk_id, device_id, shoe, round, raw_frame_id);
            """);
        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS ix_bridge_delivery_attempts_event
                ON bridge_delivery_attempts (event_id, attempted_utc DESC, attempt_id DESC);
            """);
        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS ix_bridge_recovery_requests_query
                ON bridge_recovery_requests (desk_id, last_observed_utc DESC, command_id);
            """);
        ExecuteNonQuery(connection, """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_bridge_recovery_requests_active_event_id
                ON bridge_recovery_requests (event_id)
                WHERE command_type = 'RecoverRound'
                  AND event_id IS NOT NULL
                  AND result IN ('RecoveryRequested', 'RecoveryUnconfirmed');
            """);
        ExecuteNonQuery(connection, """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_bridge_recovery_requests_active_event_uid
                ON bridge_recovery_requests (event_uid COLLATE NOCASE)
                WHERE command_type = 'RecoverRound'
                  AND event_uid IS NOT NULL
                  AND trim(event_uid) <> ''
                  AND result IN ('RecoveryRequested', 'RecoveryUnconfirmed');
            """);
        ExecuteNonQuery(connection, """
            CREATE INDEX IF NOT EXISTS ix_bridge_events_reconciliation
                ON bridge_events (reconciliation_state, next_reconcile_utc, event_id);
            """);
        BackfillRoundProjections(connection);
    }

    private static void QuarantineInvalidOrDuplicateActiveRecoveryCommands(
        SqliteConnection connection)
    {
        const string QuarantinePredicate = """
            command_type = 'RecoverRound'
            AND result IN ('RecoveryRequested', 'RecoveryUnconfirmed')
            AND
            (
                event_id IS NULL
                OR event_uid IS NULL
                OR trim(event_uid) = ''
                OR generation <= 0
                OR dispatch_count <= 0
                OR event_id IN
                (
                    SELECT event_id
                    FROM bridge_recovery_requests
                    WHERE command_type = 'RecoverRound'
                      AND result IN ('RecoveryRequested', 'RecoveryUnconfirmed')
                      AND event_id IS NOT NULL
                    GROUP BY event_id
                    HAVING COUNT(*) > 1
                )
                OR lower(trim(event_uid)) IN
                (
                    SELECT lower(trim(event_uid))
                    FROM bridge_recovery_requests
                    WHERE command_type = 'RecoverRound'
                      AND result IN ('RecoveryRequested', 'RecoveryUnconfirmed')
                      AND event_uid IS NOT NULL
                      AND trim(event_uid) <> ''
                    GROUP BY lower(trim(event_uid))
                    HAVING COUNT(*) > 1
                )
            )
            """;
        ExecuteNonQuery(connection, $$"""
            UPDATE bridge_events
            SET reconciliation_state = 'ManualReview',
                next_reconcile_utc = NULL,
                recovery_terminal_reason =
                    'Invalid or duplicate active recovery commands were quarantined during startup.'
            WHERE event_id IN
            (
                SELECT event_id
                FROM bridge_recovery_requests
                WHERE {{QuarantinePredicate}}
                  AND event_id IS NOT NULL
            );
            """);
        ExecuteNonQuery(connection, $$"""
            UPDATE bridge_recovery_requests
            SET result = 'ManualReview',
                outcome = 'ManualReview',
                next_retry_utc = NULL,
                terminal_reason =
                    'Invalid or duplicate active recovery commands were quarantined during startup.'
            WHERE {{QuarantinePredicate}};
            """);
    }

    private static void MigrateDiagnosticEventsToLocalOnly(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, """
            UPDATE bridge_events
            SET status = 'LocalOnly',
                next_retry_utc = NULL
            WHERE type NOT IN ('StartGame', 'CardDrawn', 'GameResult')
              AND status <> 'Sent';
            """);
    }

    private static void QuarantinePendingEventsFromPreviousRun(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, """
            UPDATE bridge_events
            SET status = 'Unconfirmed',
                next_retry_utc = NULL,
                last_error = CASE
                    WHEN last_error IS NULL OR trim(last_error) = ''
                        THEN 'Worker restarted before the one-shot delivery claim; automatic replay disabled.'
                    ELSE last_error
                END
            WHERE status = 'Pending';
            """);
    }

    private static void InitializeRecoveryReconciliation(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, """
            UPDATE bridge_events
            SET reconciliation_state = 'Unconfirmed',
                next_reconcile_utc = COALESCE(next_reconcile_utc, last_attempt_utc, occurred_utc),
                unconfirmed_since_utc = COALESCE(unconfirmed_since_utc, last_attempt_utc, occurred_utc)
            WHERE status = 'Unconfirmed'
              AND (reconciliation_state IS NULL OR trim(reconciliation_state) = '');
            """);
    }

    private static Guid GetOrCreateEventUid(Dictionary<string, object?> payload)
    {
        string[] eventUidKeys = payload.Keys
            .Where(key => string.Equals(key, "eventUid", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (eventUidKeys.Length == 0)
        {
            return Guid.NewGuid();
        }

        Guid? selected = null;
        foreach (string key in eventUidKeys)
        {
            Guid parsed = ParseEventUidValue(payload[key]);
            if (parsed == Guid.Empty)
            {
                throw new InvalidDataException("eventUid must be a non-empty GUID when supplied.");
            }

            if (selected.HasValue && selected.Value != parsed)
            {
                throw new InvalidDataException("Conflicting eventUid values were supplied.");
            }

            selected = parsed;
            payload.Remove(key);
        }

        return selected!.Value;
    }

    private static bool IsBmsDeliveryEvent(string type) =>
        type is "StartGame" or "CardDrawn" or "GameResult";

    private static Guid ParseEventUidValue(object? value)
    {
        return value switch
        {
            Guid guid => guid,
            string text when Guid.TryParse(text, out Guid guid) => guid,
            JsonElement element when element.ValueKind == JsonValueKind.String &&
                                     Guid.TryParse(element.GetString(), out Guid guid) => guid,
            _ => Guid.Empty
        };
    }

    private static void MigrateEventUids(SqliteConnection connection)
    {
        // Immediate transaction blocks legacy writers until backfill, constraints and validation are all installed.
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        ExecuteNonQuery(connection, transaction, """
            DROP TRIGGER IF EXISTS trg_bridge_events_event_uid_required_update;
            """);
        BackfillEventUids(connection, transaction);
        ExecuteNonQuery(connection, transaction, """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_bridge_events_event_uid
                ON bridge_events (event_uid);
            """);
        ExecuteNonQuery(connection, transaction, """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_bridge_events_event_uid_nocase
                ON bridge_events (event_uid COLLATE NOCASE);
            """);
        ExecuteNonQuery(connection, transaction, """
            CREATE TRIGGER IF NOT EXISTS trg_bridge_events_event_uid_required_insert
            BEFORE INSERT ON bridge_events
            WHEN NEW.event_uid IS NULL OR trim(NEW.event_uid) = ''
            BEGIN
                SELECT RAISE(ABORT, 'event_uid is required');
            END;
            """);
        ExecuteNonQuery(connection, transaction, """
            CREATE TRIGGER trg_bridge_events_event_uid_required_update
            BEFORE UPDATE OF event_uid ON bridge_events
            WHEN NEW.event_uid IS NULL
              OR trim(NEW.event_uid) = ''
              OR NEW.event_uid <> OLD.event_uid
            BEGIN
                SELECT RAISE(ABORT, 'event_uid is immutable');
            END;
            """);

        using SqliteCommand validation = connection.CreateCommand();
        validation.Transaction = transaction;
        validation.CommandText = """
            SELECT COUNT(*)
            FROM bridge_events
            WHERE event_uid IS NULL OR trim(event_uid) = '';
            """;
        if (Convert.ToInt64(validation.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
        {
            throw new InvalidDataException("bridge_events still contains rows without eventUid; migration stopped.");
        }

        transaction.Commit();
    }

    private static void BackfillEventUids(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        List<BridgeLegacyEventUid> events = [];
        using (SqliteCommand query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT event_id, event_uid, payload_json, status
                FROM bridge_events
                WHERE event_uid IS NULL OR trim(event_uid) = ''
                ORDER BY event_id;
                """;
            using SqliteDataReader reader = query.ExecuteReader();
            while (reader.Read())
            {
                events.Add(new BridgeLegacyEventUid(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)));
            }
        }

        foreach (BridgeLegacyEventUid stored in events)
        {
            JsonObject? payload = null;
            Guid? payloadUid = null;
            bool payloadCanBeRewritten = false;
            try
            {
                if (JsonNode.Parse(stored.PayloadJson) is JsonObject parsedPayload)
                {
                    payload = parsedPayload;
                    payloadCanBeRewritten = TryReadLegacyPayloadEventUid(payload, out payloadUid);
                }
            }
            catch (JsonException)
            {
                // 已送出的歷史壞資料不應阻止 Worker 接收新牌；未送資料會在下方隔離。
            }

            Guid? columnUid = ParseStoredEventUid(stored.EventUid);
            Guid eventUid = columnUid ?? payloadUid ?? Guid.NewGuid();
            string payloadJson = stored.PayloadJson;
            if (payload is not null && payloadCanBeRewritten)
            {
                foreach (string key in payload
                             .Select(property => property.Key)
                             .Where(key => string.Equals(key, "eventUid", StringComparison.OrdinalIgnoreCase))
                             .ToArray())
                {
                    payload.Remove(key);
                }

                payload["eventUid"] = eventUid.ToString("D");
                payloadJson = payload.ToJsonString();
            }

            bool quarantine =
                payloadUid is null &&
                stored.Status is "Pending" or "Failed";
            using SqliteCommand update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE bridge_events
                SET event_uid = $event_uid,
                    payload_json = $payload_json,
                    status = CASE WHEN $quarantine = 1 THEN 'LegacyUnconfirmed' ELSE status END,
                    next_retry_utc = CASE WHEN $quarantine = 1 THEN NULL ELSE next_retry_utc END,
                    last_error = CASE
                        WHEN $quarantine = 0 THEN last_error
                        WHEN last_error IS NULL OR trim(last_error) = '' THEN $quarantine_reason
                        ELSE last_error || ' | ' || $quarantine_reason
                    END
                WHERE event_id = $event_id;
                """;
            update.Parameters.AddWithValue("$event_uid", eventUid.ToString("D"));
            update.Parameters.AddWithValue("$payload_json", payloadJson);
            update.Parameters.AddWithValue("$quarantine", quarantine ? 1 : 0);
            update.Parameters.AddWithValue(
                "$quarantine_reason",
                "Legacy event had no stable eventUid and was quarantined during upgrade.");
            update.Parameters.AddWithValue("$event_id", stored.EventId);
            update.ExecuteNonQuery();
        }
    }

    private static bool TryReadLegacyPayloadEventUid(JsonObject payload, out Guid? eventUid)
    {
        eventUid = null;
        foreach ((string key, JsonNode? value) in payload
                     .Where(property =>
                         string.Equals(property.Key, "eventUid", StringComparison.OrdinalIgnoreCase)))
        {
            if (value is not JsonValue jsonValue ||
                !jsonValue.TryGetValue(out string? text) ||
                ParseStoredEventUid(text) is not Guid parsed)
            {
                eventUid = null;
                return false;
            }

            if (eventUid.HasValue && eventUid.Value != parsed)
            {
                eventUid = null;
                return false;
            }

            eventUid = parsed;
        }

        return true;
    }

    private static Guid? ParseStoredEventUid(string? value)
    {
        return Guid.TryParse(value, out Guid parsed) && parsed != Guid.Empty
            ? parsed
            : null;
    }

    private static void BackfillRoundProjections(SqliteConnection connection)
    {
        using (SqliteCommand count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM bridge_rounds;";
            if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                return;
            }
        }

        List<BridgeBackfillEvent> events = [];
        using (SqliteCommand query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT event_id, occurred_utc, type, desk_id, device_id, shoe, round, round_id, payload_json
                FROM bridge_events
                WHERE type IN ('StartGame', 'CardDrawn', 'GameResult')
                ORDER BY event_id;
                """;
            using SqliteDataReader reader = query.ExecuteReader();
            while (reader.Read())
            {
                events.Add(new BridgeBackfillEvent(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetInt64(7),
                    reader.GetString(8)));
            }
        }

        if (events.Count == 0)
        {
            return;
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        foreach (BridgeBackfillEvent stored in events)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(stored.PayloadJson);
                object? data = document.RootElement.TryGetProperty("data", out JsonElement dataElement)
                    ? dataElement.Clone()
                    : null;
                Dictionary<string, object?> payload = new()
                {
                    ["type"] = stored.Type,
                    ["timestamp"] = stored.OccurredUtc,
                    ["sourceDataCode"] = stored.DeskId,
                    ["deviceId"] = stored.DeviceId,
                    ["shoe"] = stored.Shoe,
                    ["round"] = stored.Round,
                    ["roundId"] = stored.RoundId,
                    ["data"] = data
                };
                string normalizedJson = JsonSerializer.Serialize(payload);
                UpdateRoundProjectionAsync(
                        connection,
                        transaction,
                        payload,
                        normalizedJson,
                        stored.EventId,
                        allowLegacyCardContract: true)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (JsonException)
            {
                // Preserve invalid legacy payloads in bridge_events without fabricating projections.
            }
            catch (InvalidDataException) when (
                string.Equals(stored.Type, "CardDrawn", StringComparison.Ordinal))
            {
                UpsertLegacyAlignmentRequiredRound(
                    connection,
                    transaction,
                    stored);
            }
        }

        transaction.Commit();
    }

    private static void UpsertLegacyAlignmentRequiredRound(
        SqliteConnection connection,
        SqliteTransaction transaction,
        BridgeBackfillEvent stored)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO bridge_rounds
                (desk_id, device_id, shoe, round, round_id, state, cards_json, updated_utc, is_complete)
            VALUES
                ($desk_id, $device_id, $shoe, $round, $round_id, 'AlignmentRequired', '[]', $updated_utc, 0)
            ON CONFLICT (desk_id, shoe, round) DO UPDATE SET
                state = CASE
                    WHEN bridge_rounds.is_complete = 1 THEN bridge_rounds.state
                    ELSE 'AlignmentRequired'
                END,
                updated_utc = CASE
                    WHEN bridge_rounds.updated_utc > excluded.updated_utc THEN bridge_rounds.updated_utc
                    ELSE excluded.updated_utc
                END;
            """;
        command.Parameters.AddWithValue("$desk_id", stored.DeskId);
        command.Parameters.AddWithValue("$device_id", stored.DeviceId);
        command.Parameters.AddWithValue("$shoe", stored.Shoe);
        command.Parameters.AddWithValue("$round", stored.Round);
        command.Parameters.AddWithValue(
            "$round_id",
            stored.RoundId.HasValue ? stored.RoundId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$updated_utc", stored.OccurredUtc);
        command.ExecuteNonQuery();
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void VerifyIntegrity(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        object? result = command.ExecuteScalar();
        if (!string.Equals(
                Convert.ToString(result, CultureInfo.InvariantCulture),
                "ok",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"SQLite integrity check failed: {Convert.ToString(result, CultureInfo.InvariantCulture)}");
        }
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
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
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30
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

    private static string TrimForStore(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = text.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 500 ? normalized : normalized[..500];
    }

    private static string RedactForStore(string text)
    {
        return BridgeDiagnosticFormatter.SanitizeForLog(
            TrimForStore(text),
            maxLength: 500);
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

    private static DateTimeOffset ParseUtcOffset(string text)
    {
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.MinValue;
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
/// <param name="EventUid">Stable event identity shared by SQLite and the HTTP payload.</param>
public sealed record BridgePendingEvent(
    long EventId,
    string Type,
    string SourceDataCode,
    string DeviceId,
    long Shoe,
    long Round,
    string PayloadJson,
    int RetryCount,
    string EventUid = "");

/// <summary>
/// Non-sensitive identity summary for one delivery-unknown event.
/// </summary>
public sealed record BridgeRecoveryCandidate(
    long EventId,
    string EventUid,
    string EventType,
    string SourceDataCode,
    string DeviceId,
    long Shoe,
    long Round,
    long? RoundId,
    DateTimeOffset AttemptedAt,
    int DecisionCount,
    string ReconciliationState);

/// <summary>Result of an exact retained GameResult lookup.</summary>
public sealed record BridgeRecoveryLookupResult(
    string Disposition,
    BridgePendingEvent? Event,
    string Message)
{
    /// <summary>Creates an exact-found result.</summary>
    public static BridgeRecoveryLookupResult Found(BridgePendingEvent pending) =>
        new("Found", pending, string.Empty);

    /// <summary>Creates a missing-result response.</summary>
    public static BridgeRecoveryLookupResult NotFound() =>
        new("NotFound", null, "The exact retained GameResult was not found.");

    /// <summary>Creates an identity-conflict response.</summary>
    public static BridgeRecoveryLookupResult Conflict(string message) =>
        new("Conflict", null, message);
}

internal sealed record BridgeLegacyEventUid(
    long EventId,
    string? EventUid,
    string PayloadJson,
    string Status);

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

/// <summary>Audit record for one observed BMS recovery or resend command decision.</summary>
public sealed record BridgeRecoveryAudit(
    string CommandId,
    string CommandType,
    string SourceDataCode,
    string DeviceId,
    long? Shoe,
    long? Round,
    long? RoundId,
    DateTimeOffset ReceivedUtc,
    DateTimeOffset ObservedUtc,
    string Result,
    DateTimeOffset? NextRetryUtc,
    string Message,
    long? EventId = null,
    string EventUid = "",
    string Outcome = "",
    int DecisionCount = 0,
    string TerminalReason = "",
    int Generation = 0,
    int DispatchCount = 0);

/// <summary>Outcome of atomically reserving a recovery command dispatch.</summary>
public enum BridgeRecoveryReservationDisposition
{
    /// <summary>The exact command dispatch is authorized to make one recovery POST.</summary>
    Authorized,

    /// <summary>The same dispatch or terminal command was already observed and must not POST again.</summary>
    Duplicate,

    /// <summary>The command conflicts with the durable event or command-generation ledger.</summary>
    Conflict
}

/// <summary>Atomic recovery-command reservation decision and non-secret diagnostic detail.</summary>
public sealed record BridgeRecoveryReservationResult(
    BridgeRecoveryReservationDisposition Disposition,
    string Message,
    bool CommandIdAlreadyExists = false)
{
    /// <summary>Creates an authorized reservation result.</summary>
    public static BridgeRecoveryReservationResult Authorized(string message) =>
        new(BridgeRecoveryReservationDisposition.Authorized, message);

    /// <summary>Creates an already-observed reservation result.</summary>
    public static BridgeRecoveryReservationResult Duplicate(string message) =>
        new(BridgeRecoveryReservationDisposition.Duplicate, message);

    /// <summary>Creates a conflicting reservation result.</summary>
    public static BridgeRecoveryReservationResult Conflict(
        string message,
        bool commandIdAlreadyExists = false) =>
        new(
            BridgeRecoveryReservationDisposition.Conflict,
            message,
            commandIdAlreadyExists);
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

internal sealed record BridgeRoundCardProjection(
    string? Target,
    int Index,
    string? Suit,
    string? Value,
    long EventId,
    string OccurredUtc);

internal sealed record BridgeBackfillEvent(
    long EventId,
    string OccurredUtc,
    string Type,
    string DeskId,
    string DeviceId,
    long Shoe,
    long Round,
    long? RoundId,
    string PayloadJson);
