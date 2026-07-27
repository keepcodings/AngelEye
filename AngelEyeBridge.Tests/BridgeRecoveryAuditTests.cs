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
    public async Task TerminalNotFound_DoesNotRegressToLegacyOrHandledStates()
    {
        BridgeEventJournal journal = new(_dbPath);
        DateTimeOffset received = new(2026, 7, 23, 3, 0, 0, TimeSpan.Zero);
        await journal.RecordRecoveryRequestAsync(Audit("NotFound", received.AddSeconds(5), received.AddSeconds(10), "missing"));
        await journal.RecordRecoveryRequestAsync(Audit("RecoveryRequested", received.AddSeconds(15), null, "requested"));
        await journal.RecordRecoveryRequestAsync(Audit("Handled", received.AddSeconds(20), null, "duplicate"));

        using SqliteConnection connection = Open();
        using SqliteCommand query = connection.CreateCommand();
        query.CommandText = """
            SELECT result, received_utc, last_observed_utc, next_retry_utc, message
            FROM bridge_recovery_requests WHERE command_id = 'recover-93';
            """;
        using SqliteDataReader reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("NotFound", reader.GetString(0));
        Assert.StartsWith("2026-07-23T03:00:00", reader.GetString(1));
        Assert.StartsWith("2026-07-23T03:00:20", reader.GetString(2));
        Assert.True(reader.IsDBNull(3));
        Assert.Equal("missing", reader.GetString(4));
        Assert.False(reader.Read());
    }

    [Fact]
    public async Task StaleAuditCannotSynthesizeAnUnauthorizedGenerationDispatchTuple()
    {
        BridgeEventJournal journal = new(_dbPath);
        DateTimeOffset observed = new(2026, 7, 23, 4, 0, 0, TimeSpan.Zero);
        BridgeRecoveryAudit current = Audit(
            "RecoveryUnconfirmed",
            observed,
            observed.AddMinutes(1),
            "ack unknown") with
        {
            Generation = 2,
            DispatchCount = 1
        };
        await journal.RecordRecoveryRequestAsync(current);
        await journal.RecordRecoveryRequestAsync(current with
        {
            Result = "Recovered",
            Outcome = "Recovered",
            Generation = 1,
            DispatchCount = 5,
            ObservedUtc = observed.AddSeconds(10)
        });

        using SqliteConnection connection = Open();
        using SqliteCommand query = connection.CreateCommand();
        query.CommandText = """
            SELECT result, generation, dispatch_count
            FROM bridge_recovery_requests
            WHERE command_id = 'recover-93';
            """;
        using SqliteDataReader reader = query.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("RecoveryUnconfirmed", reader.GetString(0));
        Assert.Equal(2, reader.GetInt32(1));
        Assert.Equal(1, reader.GetInt32(2));
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
