using AngelEyeBmsBridge;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class BridgeRoundProjectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "angel-eye-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public BridgeRoundProjectionTests()
    {
        Directory.CreateDirectory(_directory);
        _dbPath = Path.Combine(_directory, "projection.sqlite");
    }

    [Fact]
    public async Task CompleteRound_IsProjectedWithOrderedCardsAndResult()
    {
        BridgeEventJournal journal = new(_dbPath);
        long shoe = 202607230001;
        await journal.AppendAsync(Payload("StartGame", shoe, 93, "2026-07-22T15:59:58Z", new { startTime = "2026-07-22T15:59:58Z" }));
        await journal.AppendAsync(Payload("CardDrawn", shoe, 93, "2026-07-22T15:59:59Z", new { target = "Player", index = 1, suit = "Spade", value = "8" }));
        await journal.AppendAsync(Payload("CardDrawn", shoe, 93, "2026-07-22T16:00:02Z", new { target = "Banker", index = 1, suit = "Heart", value = "K" }));
        await journal.AppendAsync(Payload("GameResult", shoe, 93, "2026-07-22T16:00:10Z", new { result = "Player", pair = "None" }));

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT state, started_utc, settled_utc, cards_json, result_json, start_event_id, result_event_id, is_complete
            FROM bridge_rounds WHERE desk_id = '901' AND shoe = $shoe AND round = 93;
            """;
        command.Parameters.AddWithValue("$shoe", shoe);
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Settled", reader.GetString(0));
        Assert.StartsWith("2026-07-22T15:59:58", reader.GetString(1));
        Assert.StartsWith("2026-07-22T16:00:10", reader.GetString(2));
        using JsonDocument cards = JsonDocument.Parse(reader.GetString(3));
        Assert.Equal(2, cards.RootElement.GetArrayLength());
        Assert.Equal("Player", cards.RootElement[0].GetProperty("Target").GetString());
        Assert.Equal("Banker", cards.RootElement[1].GetProperty("Target").GetString());
        using JsonDocument result = JsonDocument.Parse(reader.GetString(4));
        Assert.Equal("Player", result.RootElement.GetProperty("result").GetString());
        Assert.Equal(1, reader.GetInt64(5));
        Assert.Equal(4, reader.GetInt64(6));
        Assert.Equal(1, reader.GetInt32(7));
    }

    [Fact]
    public async Task CardWithoutResult_RemainsQueryableAndIncomplete()
    {
        BridgeEventJournal journal = new(_dbPath);
        await journal.AppendAsync(Payload("CardDrawn", 202607230001, 94, "2026-07-23T01:00:00Z", new { target = "Player", index = 1, suit = "Club", value = "2" }));

        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT state, result_json, is_complete FROM bridge_rounds WHERE desk_id = '901' AND round = 94;";
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Dealing", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal(0, reader.GetInt32(2));
    }

    [Fact]
    public async Task ProjectionFailure_RollsBackEventAndRound()
    {
        BridgeEventJournal journal = new(_dbPath);
        using (SqliteConnection connection = Open())
        using (SqliteCommand trigger = connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER fail_round_projection
                BEFORE INSERT ON bridge_rounds
                BEGIN
                    SELECT RAISE(ABORT, 'projection failed');
                END;
                """;
            trigger.ExecuteNonQuery();
        }

        await Assert.ThrowsAsync<SqliteException>(() => journal.AppendAsync(
            Payload("StartGame", 202607230002, 1, "2026-07-23T16:00:00Z", new { startTime = "2026-07-23T16:00:00Z" })));

        using SqliteConnection verify = Open();
        Assert.Equal(0L, ScalarCount(verify, "bridge_events"));
        Assert.Equal(0L, ScalarCount(verify, "bridge_rounds"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static Dictionary<string, object?> Payload(string type, long shoe, long round, string timestamp, object data) => new()
    {
        ["type"] = type,
        ["source"] = "ANGEL",
        ["timestamp"] = timestamp,
        ["sourceDataCode"] = "901",
        ["deviceId"] = "901",
        ["shoe"] = shoe,
        ["round"] = round,
        ["roundId"] = round,
        ["data"] = data
    };

    private SqliteConnection Open()
    {
        SqliteConnection connection = new($"Data Source={_dbPath}");
        connection.Open();
        return connection;
    }

    private static long ScalarCount(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)(command.ExecuteScalar() ?? 0L);
    }
}
