using AngelEyeBmsBridge;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class BridgeDeliveryAttemptTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "angel-eye-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public BridgeDeliveryAttemptTests()
    {
        Directory.CreateDirectory(_directory);
        _dbPath = Path.Combine(_directory, "attempts.sqlite");
    }

    [Fact]
    public async Task SuccessAndFailure_AreAuditedInOrder_WithSecretsRedacted()
    {
        BridgeEventJournal journal = new(_dbPath);
        long eventId = await journal.AppendAsync(Payload());
        DateTime failedAt = new(2026, 7, 23, 2, 0, 0, DateTimeKind.Utc);
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhZG1pbiJ9.signature";
        await journal.MarkFailedAsync(
            eventId,
            retryCount: 1,
            failedAt,
            $"503 upstream Authorization: Bearer secret-token jwt={jwt} signingKey=top-secret",
            httpStatus: 503);
        await journal.MarkSentAsync(eventId, failedAt.AddSeconds(2), httpStatus: 200);

        using SqliteConnection connection = Open();
        using SqliteCommand query = connection.CreateCommand();
        query.CommandText = """
            SELECT succeeded, http_status, retry_count, next_retry_utc, error
            FROM bridge_delivery_attempts
            WHERE event_id = $event_id
            ORDER BY attempt_id;
            """;
        query.Parameters.AddWithValue("$event_id", eventId);
        using SqliteDataReader reader = query.ExecuteReader();

        Assert.True(reader.Read());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal(503, reader.GetInt32(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.False(reader.IsDBNull(3));
        string error = reader.GetString(4);
        Assert.DoesNotContain("secret-token", error, StringComparison.Ordinal);
        Assert.DoesNotContain(jwt, error, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", error, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", error, StringComparison.Ordinal);

        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(200, reader.GetInt32(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.True(reader.IsDBNull(3));
        Assert.True(reader.IsDBNull(4));
        Assert.False(reader.Read());

        using SqliteCommand current = connection.CreateCommand();
        current.CommandText = "SELECT status, retry_count, last_error FROM bridge_events WHERE event_id = $event_id;";
        current.Parameters.AddWithValue("$event_id", eventId);
        using SqliteDataReader currentReader = current.ExecuteReader();
        Assert.True(currentReader.Read());
        Assert.Equal("Sent", currentReader.GetString(0));
        Assert.Equal(1, currentReader.GetInt32(1));
        Assert.True(currentReader.IsDBNull(2));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static Dictionary<string, object?> Payload() => new()
    {
        ["type"] = "StartGame",
        ["source"] = "ANGEL",
        ["timestamp"] = "2026-07-23T02:00:00Z",
        ["sourceDataCode"] = "901",
        ["deviceId"] = "901",
        ["shoe"] = 202607230001,
        ["round"] = 1,
        ["data"] = new { startTime = "2026-07-23T02:00:00Z" }
    };

    private SqliteConnection Open()
    {
        SqliteConnection connection = new($"Data Source={_dbPath}");
        connection.Open();
        return connection;
    }
}
