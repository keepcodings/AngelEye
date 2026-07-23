using AngelEyeBmsBridge;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class BridgeRecoveryAuditTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "angel-eye-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public BridgeRecoveryAuditTests()
    {
        Directory.CreateDirectory(_directory);
        _dbPath = Path.Combine(_directory, "recoveries.sqlite");
    }

    [Fact]
    public async Task DuplicateCommand_AdvancesToRequeued_AndDoesNotRegress()
    {
        BridgeEventJournal journal = new(_dbPath);
        DateTimeOffset received = new(2026, 7, 23, 3, 0, 0, TimeSpan.Zero);
        await journal.RecordRecoveryRequestAsync(Audit("NotFound", received.AddSeconds(5), received.AddSeconds(10), "missing"));
        await journal.RecordRecoveryRequestAsync(Audit("Requeued", received.AddSeconds(15), null, "requeued"));
        await journal.RecordRecoveryRequestAsync(Audit("Handled", received.AddSeconds(20), null, "duplicate"));

        using SqliteConnection connection = Open();
        using SqliteCommand query = connection.CreateCommand();
        query.CommandText = """
            SELECT result, received_utc, last_observed_utc, next_retry_utc, message
            FROM bridge_recovery_requests WHERE command_id = 'recover-93';
            """;
        using SqliteDataReader reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("Requeued", reader.GetString(0));
        Assert.StartsWith("2026-07-23T03:00:00", reader.GetString(1));
        Assert.StartsWith("2026-07-23T03:00:20", reader.GetString(2));
        Assert.True(reader.IsDBNull(3));
        Assert.Equal("requeued", reader.GetString(4));
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

    private static BridgeRecoveryAudit Audit(
        string result,
        DateTimeOffset observed,
        DateTimeOffset? nextRetry,
        string message) => new(
            "recover-93",
            "RecoverRound",
            "901",
            "901",
            202607230001,
            93,
            93,
            new DateTimeOffset(2026, 7, 23, 3, 0, 0, TimeSpan.Zero),
            observed,
            result,
            nextRetry,
            message);

    private SqliteConnection Open()
    {
        SqliteConnection connection = new($"Data Source={_dbPath}");
        connection.Open();
        return connection;
    }
}
