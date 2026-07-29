using AngelEyeBmsBridge;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class BridgeEventJournalMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "angel-eye-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExistingJournal_IsUpgradedIdempotently_WithoutChangingEvents()
    {
        Directory.CreateDirectory(_directory);
        string dbPath = Path.Combine(_directory, "legacy.sqlite");
        CreateLegacyJournal(dbPath);

        _ = new BridgeEventJournal(dbPath);
        _ = new BridgeEventJournal(dbPath);

        using SqliteConnection connection = Open(dbPath);
        using SqliteCommand events = connection.CreateCommand();
        events.CommandText = "SELECT event_id, status, retry_count, last_error, event_uid, payload_json FROM bridge_events ORDER BY event_id;";
        using SqliteDataReader reader = events.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(41, reader.GetInt64(0));
        Assert.Equal("Sent", reader.GetString(1));
        Assert.Equal(2, reader.GetInt32(2));
        Assert.Equal("legacy", reader.GetString(3));
        string eventUid = reader.GetString(4);
        Assert.NotEqual(Guid.Empty, Guid.Parse(eventUid));
        using (JsonDocument payload = JsonDocument.Parse(reader.GetString(5)))
        {
            Assert.Equal(eventUid, payload.RootElement.GetProperty("eventUid").GetString());
        }
        Assert.False(reader.Read());
        reader.Close();

        string[] expectedTables = ["bridge_rounds", "bridge_delivery_attempts", "bridge_recovery_requests"];
        foreach (string table in expectedTables)
        {
            using SqliteCommand tableQuery = connection.CreateCommand();
            tableQuery.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
            tableQuery.Parameters.AddWithValue("$name", table);
            Assert.Equal(1L, tableQuery.ExecuteScalar());
        }

        string[] expectedIndexes =
        [
            "ix_bridge_rounds_query",
            "ix_bridge_rounds_state",
            "ix_bridge_delivery_attempts_event",
            "ix_bridge_recovery_requests_query",
            "ux_bridge_recovery_requests_active_event_id",
            "ux_bridge_recovery_requests_active_event_uid",
            "ux_bridge_events_event_uid",
            "ux_bridge_events_event_uid_nocase"
        ];
        foreach (string index in expectedIndexes)
        {
            using SqliteCommand indexQuery = connection.CreateCommand();
            indexQuery.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;";
            indexQuery.Parameters.AddWithValue("$name", index);
            Assert.Equal(1L, indexQuery.ExecuteScalar());
        }
    }

    [Fact]
    public void ExistingPayloadEventUid_IsPreservedDuringUpgrade()
    {
        Directory.CreateDirectory(_directory);
        string dbPath = Path.Combine(_directory, "legacy-with-event-uid.sqlite");
        Guid expectedEventUid = Guid.NewGuid();
        CreateLegacyJournal(dbPath, $$"""{"eventUid":"{{expectedEventUid:D}}"}""");

        _ = new BridgeEventJournal(dbPath);

        using SqliteConnection connection = Open(dbPath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT event_uid, payload_json FROM bridge_events WHERE event_id = 41;";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(expectedEventUid.ToString("D"), reader.GetString(0));
        using JsonDocument payload = JsonDocument.Parse(reader.GetString(1));
        Assert.Equal(expectedEventUid.ToString("D"), payload.RootElement.GetProperty("eventUid").GetString());
    }

    [Fact]
    public async Task LegacyUnsentEventWithoutStableEventUid_IsQuarantined()
    {
        Directory.CreateDirectory(_directory);
        string dbPath = Path.Combine(_directory, "legacy-pending.sqlite");
        CreateLegacyJournal(dbPath, "{}", "Failed");

        BridgeEventJournal journal = new(dbPath);

        using (SqliteConnection connection = Open(dbPath))
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT status, next_retry_utc, last_error FROM bridge_events WHERE event_id = 41;";
            using SqliteDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("LegacyUnconfirmed", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
            Assert.Contains("quarantined", reader.GetString(2));
        }

        Assert.Empty(await journal.GetDueOutboxEventsAsync(20, DateTime.UtcNow.AddYears(1)));
    }

    [Fact]
    public async Task DuplicateActiveRecoveryCommands_AreQuarantinedBeforeUniqueIndexesAreCreated()
    {
        Directory.CreateDirectory(_directory);
        string dbPath = Path.Combine(_directory, "duplicate-active-recoveries.sqlite");
        BridgeEventJournal journal = new(dbPath);
        Guid eventUid = Guid.NewGuid();
        long eventId = await journal.AppendAsync(new Dictionary<string, object?>
        {
            ["eventUid"] = eventUid,
            ["type"] = "GameResult",
            ["source"] = "ANGEL",
            ["timestamp"] = "2026-07-26T02:52:00Z",
            ["sourceDataCode"] = "901",
            ["deviceId"] = "SHOE901",
            ["shoe"] = 202607260001,
            ["round"] = 12,
            ["roundId"] = 9012,
            ["data"] = new { status = "Normal" }
        });
        DateTime attempt = new(2026, 7, 26, 2, 52, 0, DateTimeKind.Utc);
        Assert.True(await journal.TryClaimForDeliveryAsync(eventId, attempt));
        await journal.MarkUnconfirmedAsync(eventId, 1, attempt, "timeout");

        using (SqliteConnection connection = Open(dbPath))
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                DROP INDEX ux_bridge_recovery_requests_active_event_id;
                DROP INDEX ux_bridge_recovery_requests_active_event_uid;
                INSERT INTO bridge_recovery_requests
                    (command_id, command_type, desk_id, device_id, shoe, round, round_id,
                     received_utc, last_observed_utc, result, event_id, event_uid,
                     generation, dispatch_count)
                VALUES
                    ('recover-duplicate-a', 'RecoverRound', '901', 'SHOE901',
                     202607260001, 12, 9012,
                     '2026-07-26T02:53:00Z', '2026-07-26T02:53:00Z',
                     'RecoveryRequested', $event_id, $event_uid, 1, 1),
                    ('recover-duplicate-b', 'RecoverRound', '901', 'SHOE901',
                     202607260001, 12, 9012,
                     '2026-07-26T02:54:00Z', '2026-07-26T02:54:00Z',
                     'RecoveryUnconfirmed', $event_id, $event_uid, 1, 1);
                """;
            command.Parameters.AddWithValue("$event_id", eventId);
            command.Parameters.AddWithValue("$event_uid", eventUid.ToString("D"));
            command.ExecuteNonQuery();
        }

        _ = new BridgeEventJournal(dbPath);

        using SqliteConnection reopened = Open(dbPath);
        using (SqliteCommand requests = reopened.CreateCommand())
        {
            requests.CommandText = """
                SELECT result, outcome, terminal_reason
                FROM bridge_recovery_requests
                ORDER BY command_id;
                """;
            using SqliteDataReader reader = requests.ExecuteReader();
            for (int index = 0; index < 2; index++)
            {
                Assert.True(reader.Read());
                Assert.Equal("ManualReview", reader.GetString(0));
                Assert.Equal("ManualReview", reader.GetString(1));
                Assert.Contains("quarantined", reader.GetString(2), StringComparison.OrdinalIgnoreCase);
            }
            Assert.False(reader.Read());
        }

        using (SqliteCommand eventState = reopened.CreateCommand())
        {
            eventState.CommandText = """
                SELECT reconciliation_state, recovery_terminal_reason
                FROM bridge_events
                WHERE event_id = $event_id;
                """;
            eventState.Parameters.AddWithValue("$event_id", eventId);
            using SqliteDataReader reader = eventState.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("ManualReview", reader.GetString(0));
            Assert.Contains("quarantined", reader.GetString(1), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void MalformedSentPayload_DoesNotBlockWorkerStartup()
    {
        Directory.CreateDirectory(_directory);
        string dbPath = Path.Combine(_directory, "legacy-malformed-sent.sqlite");
        CreateLegacyJournal(dbPath, "{not-json", "Sent");

        _ = new BridgeEventJournal(dbPath);

        using SqliteConnection connection = Open(dbPath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT event_uid, payload_json, status FROM bridge_events WHERE event_id = 41;";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.NotEqual(Guid.Empty, Guid.Parse(reader.GetString(0)));
        Assert.Equal("{not-json", reader.GetString(1));
        Assert.Equal("Sent", reader.GetString(2));
    }

    [Fact]
    public async Task LegacyPendingCardDrawn_IsQuarantinedAndNotReplayedAfterStartup()
    {
        Directory.CreateDirectory(_directory);
        string dbPath = Path.Combine(_directory, "legacy-diagnostic.sqlite");
        Guid eventUid = Guid.NewGuid();
        CreateLegacyJournal(
            dbPath,
            $$"""{"eventUid":"{{eventUid:D}}"}""",
            status: "Pending",
            type: "CardDrawn");

        BridgeEventJournal journal = new(dbPath);

        using (SqliteConnection connection = Open(dbPath))
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT status, next_retry_utc FROM bridge_events WHERE event_id = 41;";
            using SqliteDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("Unconfirmed", reader.GetString(0));
            Assert.True(reader.IsDBNull(1));
        }

        Assert.Empty(await journal.GetDueOutboxEventsAsync(20, DateTime.UtcNow.AddYears(1)));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static void CreateLegacyJournal(
        string dbPath,
        string payloadJson = "{}",
        string status = "Sent",
        string type = "GameResult")
    {
        using SqliteConnection connection = Open(dbPath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE bridge_events
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
                payload_json TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'Pending',
                retry_count INTEGER NOT NULL DEFAULT 0,
                next_retry_utc TEXT NULL,
                last_attempt_utc TEXT NULL,
                sent_utc TEXT NULL,
                last_error TEXT NULL
            );
            INSERT INTO bridge_events
                (event_id, occurred_utc, type, source, desk_id, device_id, shoe, round, payload_json, status, retry_count, last_error)
            VALUES
                (41, '2026-07-22T15:59:58.0000000Z', $type, 'ANGEL', '901', '901', 202607220001, 93, $payload_json, $status, 2, 'legacy');
            """;
        command.Parameters.AddWithValue("$payload_json", payloadJson);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$type", type);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string dbPath)
    {
        SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        return connection;
    }
}
