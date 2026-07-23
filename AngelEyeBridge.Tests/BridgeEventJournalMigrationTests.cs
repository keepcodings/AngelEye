using AngelEyeBmsBridge;
using Microsoft.Data.Sqlite;
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
        events.CommandText = "SELECT event_id, status, retry_count, last_error FROM bridge_events ORDER BY event_id;";
        using SqliteDataReader reader = events.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(41, reader.GetInt64(0));
        Assert.Equal("Sent", reader.GetString(1));
        Assert.Equal(2, reader.GetInt32(2));
        Assert.Equal("legacy", reader.GetString(3));
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
            "ix_bridge_recovery_requests_query"
        ];
        foreach (string index in expectedIndexes)
        {
            using SqliteCommand indexQuery = connection.CreateCommand();
            indexQuery.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = $name;";
            indexQuery.Parameters.AddWithValue("$name", index);
            Assert.Equal(1L, indexQuery.ExecuteScalar());
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static void CreateLegacyJournal(string dbPath)
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
                (41, '2026-07-22T15:59:58.0000000Z', 'GameResult', 'ANGEL', '901', '901', 202607220001, 93, '{}', 'Sent', 2, 'legacy');
            """;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string dbPath)
    {
        SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        return connection;
    }
}
