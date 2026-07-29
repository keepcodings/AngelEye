using AngelEyeBmsBridge;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class BridgeGameResultDeliveryGateTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "angel-eye-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public BridgeGameResultDeliveryGateTests()
    {
        Directory.CreateDirectory(_directory);
        _dbPath = Path.Combine(_directory, "delivery-gate.sqlite");
    }

    [Fact]
    public async Task RejectedStartGame_MarksPendingResultUnregisteredSkipped()
    {
        BridgeEventJournal journal = new(_dbPath);
        long startEventId = await journal.AppendAsync(Payload("StartGame"));
        Assert.True(await journal.TryClaimForDeliveryAsync(startEventId, DateTime.UtcNow));
        await journal.MarkRejectedAsync(startEventId, 1, DateTime.UtcNow, "400 rejected", 400);
        long resultEventId = await journal.AppendAsync(Payload("GameResult"));

        bool deliver = await journal.PrepareGameResultForDeliveryAsync(resultEventId);

        Assert.False(deliver);
        Assert.Equal("Rejected", ReadStatus(startEventId));
        Assert.Equal("UnregisteredSkipped", ReadStatus(resultEventId));
        Assert.Empty(await journal.GetDueOutboxEventsAsync(20, DateTime.UtcNow));
    }

    [Fact]
    public async Task UnconfirmedStartGame_AllowsOneCurrentGameResultAttempt()
    {
        BridgeEventJournal journal = new(_dbPath);
        long startEventId = await journal.AppendAsync(Payload("StartGame"));
        Assert.True(await journal.TryClaimForDeliveryAsync(startEventId, DateTime.UtcNow));
        await journal.MarkUnconfirmedAsync(startEventId, 1, DateTime.UtcNow, "timeout");
        long resultEventId = await journal.AppendAsync(Payload("GameResult"));

        Assert.True(await journal.PrepareGameResultForDeliveryAsync(resultEventId));
        BridgePendingEvent due = Assert.Single(
            await journal.GetDueOutboxEventsAsync(20, DateTime.UtcNow));
        Assert.Equal(resultEventId, due.EventId);
    }

    [Fact]
    public async Task SentStartGame_RemainsDeliverableAfterJournalReopen()
    {
        BridgeEventJournal journal = new(_dbPath);
        long startEventId = await journal.AppendAsync(Payload("StartGame"));
        Assert.True(await journal.TryClaimForDeliveryAsync(startEventId, DateTime.UtcNow));
        await journal.MarkSentAsync(startEventId, DateTime.UtcNow, 200);

        BridgeEventJournal reopened = new(_dbPath);

        Assert.True(await reopened.HasDeliverableStartGameAsync(
            "901",
            "SHOE901",
            202607260001,
            7,
            7));
    }

    [Fact]
    public async Task CardDrawn_RequiresSentStartGame_AndIsNeverARecoveryCandidate()
    {
        BridgeEventJournal journal = new(_dbPath);
        long startEventId = await journal.AppendAsync(Payload("StartGame"));
        long cardEventId = await journal.AppendAsync(Payload("CardDrawn"));

        Assert.False(await journal.PrepareCardDrawnForDeliveryAsync(cardEventId));
        Assert.Equal("Pending", ReadStatus(cardEventId));

        Assert.True(await journal.TryClaimForDeliveryAsync(startEventId, DateTime.UtcNow));
        await journal.MarkSentAsync(startEventId, DateTime.UtcNow, 200);

        Assert.True(await journal.PrepareCardDrawnForDeliveryAsync(cardEventId));
        Assert.True(await journal.TryClaimForDeliveryAsync(cardEventId, DateTime.UtcNow));
        await journal.MarkUnconfirmedAsync(cardEventId, 1, DateTime.UtcNow, "timeout");

        Assert.Empty(
            await journal.GetDueRecoveryCandidatesAsync(
                20,
                DateTimeOffset.UtcNow.AddDays(1)));
    }

    [Fact]
    public async Task RejectedStartGame_MakesCardDrawnLocalOnly()
    {
        BridgeEventJournal journal = new(_dbPath);
        long startEventId = await journal.AppendAsync(Payload("StartGame"));
        Assert.True(await journal.TryClaimForDeliveryAsync(startEventId, DateTime.UtcNow));
        await journal.MarkRejectedAsync(startEventId, 1, DateTime.UtcNow, "400 rejected", 400);
        long cardEventId = await journal.AppendAsync(Payload("CardDrawn"));

        Assert.False(await journal.PrepareCardDrawnForDeliveryAsync(cardEventId));
        Assert.Equal("LocalOnly", ReadStatus(cardEventId));
        Assert.Empty(await journal.GetDueOutboxEventsAsync(20, DateTime.UtcNow));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static Dictionary<string, object?> Payload(string type) => new()
    {
        ["type"] = type,
        ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
        ["source"] = "ANGEL",
        ["sourceDataCode"] = "901",
        ["deviceId"] = "SHOE901",
        ["shoe"] = 202607260001,
        ["round"] = 7,
        ["roundId"] = 7,
        ["data"] = new { }
    };

    private string ReadStatus(long eventId)
    {
        using SqliteConnection connection = new($"Data Source={_dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM bridge_events WHERE event_id = $event_id;";
        command.Parameters.AddWithValue("$event_id", eventId);
        return Assert.IsType<string>(command.ExecuteScalar());
    }
}
