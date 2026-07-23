using AngelEyeBmsBridge;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class BridgeRoundBackfillTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "angel-eye-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public BridgeRoundBackfillTests()
    {
        Directory.CreateDirectory(_directory);
        _dbPath = Path.Combine(_directory, "backfill.sqlite");
    }

    [Fact]
    public void LegacyEvents_AreBackfilledOnce_WithoutInventingMissingResult()
    {
        CreateLegacyEvents();

        _ = new BridgeEventJournal(_dbPath);
        _ = new BridgeEventJournal(_dbPath);

        using SqliteConnection connection = Open();
        using SqliteCommand query = connection.CreateCommand();
        query.CommandText = """
            SELECT round, state, cards_json, result_json, is_complete
            FROM bridge_rounds
            ORDER BY round;
            """;
        using SqliteDataReader reader = query.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(7, reader.GetInt64(0));
        Assert.Equal("Settled", reader.GetString(1));
        using (JsonDocument cards = JsonDocument.Parse(reader.GetString(2)))
        {
            Assert.Single(cards.RootElement.EnumerateArray());
            Assert.Equal("Player", cards.RootElement[0].GetProperty("Target").GetString());
        }
        Assert.Contains("Banker", reader.GetString(3), StringComparison.Ordinal);
        Assert.Equal(1, reader.GetInt32(4));

        Assert.True(reader.Read());
        Assert.Equal(8, reader.GetInt64(0));
        Assert.Equal("Dealing", reader.GetString(1));
        Assert.True(reader.IsDBNull(3));
        Assert.Equal(0, reader.GetInt32(4));
        Assert.False(reader.Read());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private void CreateLegacyEvents()
    {
        using SqliteConnection connection = Open();
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
            INSERT INTO bridge_events VALUES
                (1, '2026-07-23T01:00:00Z', 'StartGame', 'ANGEL', '901', '901', 202607230001, 7, 7,
                 '{"data":{"startTime":"2026-07-23T01:00:00Z"}}', 'Sent', 0, NULL, NULL, NULL, NULL),
                (2, '2026-07-23T01:00:03Z', 'CardDrawn', 'ANGEL', '901', '901', 202607230001, 7, 7,
                 '{"data":{"target":"Player","index":1,"suit":"Diamond","value":"9"}}', 'Sent', 0, NULL, NULL, NULL, NULL),
                (3, '2026-07-23T01:00:18Z', 'GameResult', 'ANGEL', '901', '901', 202607230001, 7, 7,
                 '{"data":{"result":"Banker","pair":"None"}}', 'Sent', 0, NULL, NULL, NULL, NULL),
                (4, '2026-07-23T01:01:00Z', 'CardDrawn', 'ANGEL', '901', '901', 202607230001, 8, 8,
                 '{"data":{"target":"Player","index":1}}', 'Pending', 0, NULL, NULL, NULL, NULL);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        SqliteConnection connection = new($"Data Source={_dbPath}");
        connection.Open();
        return connection;
    }
}
