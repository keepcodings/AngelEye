using System.Text.Json;
using AngelEyeBmsBridge;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class BridgeEventUidTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "angel-eye-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FirstLocalEventAcrossTwoJournals_HasDistinctEventUid()
    {
        Directory.CreateDirectory(_directory);
        BridgeEventJournal first = new(Path.Combine(_directory, "901.sqlite"));
        BridgeEventJournal second = new(Path.Combine(_directory, "902.sqlite"));

        long firstEventId = await first.AppendAsync(Payload("901"));
        long secondEventId = await second.AppendAsync(Payload("902"));
        string firstUid = ReadEventUid(first.DbPath, firstEventId);
        string secondUid = ReadEventUid(second.DbPath, secondEventId);

        Assert.Equal(1, firstEventId);
        Assert.Equal(1, secondEventId);
        Assert.NotEqual(firstUid, secondUid);
        Assert.NotEqual(Guid.Empty, Guid.Parse(firstUid));
        Assert.NotEqual(Guid.Empty, Guid.Parse(secondUid));
    }

    [Fact]
    public async Task Append_PersistsSameEventUidInColumnPayloadAndPendingRecord()
    {
        Directory.CreateDirectory(_directory);
        BridgeEventJournal journal = new(Path.Combine(_directory, "journal.sqlite"));
        Dictionary<string, object?> payload = Payload("903");

        long eventId = await journal.AppendAsync(payload);
        BridgePendingEvent pending = Assert.Single(
            await journal.GetDueOutboxEventsAsync(20, DateTime.UtcNow.AddMinutes(1)));
        string columnUid = ReadEventUid(journal.DbPath, eventId);
        using JsonDocument document = JsonDocument.Parse(pending.PayloadJson);
        string payloadUid = document.RootElement.GetProperty("eventUid").GetString()!;

        Assert.Equal(columnUid, payloadUid);
        Assert.Equal(columnUid, pending.EventUid);
        Assert.Equal(columnUid, payload["eventUid"]!.ToString(), ignoreCase: true);
    }

    [Fact]
    public async Task ReopenAndDeliveryStateChanges_PreserveEventUidAndPayload()
    {
        Directory.CreateDirectory(_directory);
        string dbPath = Path.Combine(_directory, "reopen.sqlite");
        BridgeEventJournal journal = new(dbPath);
        long eventId = await journal.AppendAsync(Payload("901"));
        BridgePendingEvent initial = Assert.Single(
            await journal.GetDueOutboxEventsAsync(20, DateTime.UtcNow.AddMinutes(1)));
        Assert.True(await journal.TryClaimForDeliveryAsync(eventId, DateTime.UtcNow));
        await journal.MarkFailedAsync(eventId, 1, DateTime.UtcNow, "timeout");

        BridgeEventJournal reopened = new(dbPath);
        Assert.Empty(await reopened.GetDueOutboxEventsAsync(20, DateTime.UtcNow.AddDays(1)));
        string afterReopenEventUid = ReadEventUid(dbPath, eventId);

        Assert.Equal(initial.EventUid, afterReopenEventUid);
        Assert.Equal(initial.PayloadJson, ReadPayloadJson(dbPath, eventId));
        Assert.Equal("Failed", ReadEventStatus(dbPath, eventId));
    }

    [Fact]
    public async Task FailedEvent_IsNotRetriedAndDoesNotBlockNewerEvent()
    {
        Directory.CreateDirectory(_directory);
        BridgeEventJournal journal = new(Path.Combine(_directory, "single-attempt.sqlite"));
        long firstEventId = await journal.AppendAsync(Payload("901"));
        long secondEventId = await journal.AppendAsync(Payload("901"));

        BridgePendingEvent firstAttempt = Assert.Single(
            await journal.GetDueOutboxEventsAsync(20, DateTime.UtcNow));
        Assert.Equal(firstEventId, firstAttempt.EventId);
        Assert.True(await journal.TryClaimForDeliveryAsync(firstEventId, DateTime.UtcNow));
        await journal.MarkFailedAsync(firstEventId, 1, DateTime.UtcNow, "timeout");

        BridgePendingEvent nextAttempt = Assert.Single(
            await journal.GetDueOutboxEventsAsync(20, DateTime.UtcNow.AddYears(1)));

        Assert.Equal(secondEventId, nextAttempt.EventId);
        Assert.NotEqual(firstAttempt.EventUid, nextAttempt.EventUid);
    }

    [Fact]
    public async Task Restart_QuarantinesClaimedAndNeverAttemptedPendingEvents()
    {
        Directory.CreateDirectory(_directory);
        string dbPath = Path.Combine(_directory, "crash-window.sqlite");
        BridgeEventJournal journal = new(dbPath);
        long firstEventId = await journal.AppendAsync(Payload("902"));
        long secondEventId = await journal.AppendAsync(Payload("902"));

        Assert.True(await journal.TryClaimForDeliveryAsync(firstEventId, DateTime.UtcNow));

        BridgeEventJournal reopened = new(dbPath);
        Assert.Empty(await reopened.GetDueOutboxEventsAsync(20, DateTime.UtcNow.AddYears(1)));

        Assert.Equal("Unconfirmed", ReadEventStatus(dbPath, firstEventId));
        Assert.Equal("Unconfirmed", ReadEventStatus(dbPath, secondEventId));
    }

    [Fact]
    public async Task LocalOnlyEvent_IsQueryableButNeverDueOrCountedAsOutbox()
    {
        Directory.CreateDirectory(_directory);
        BridgeEventJournal journal = new(Path.Combine(_directory, "local-only.sqlite"));

        long eventId = await journal.AppendAsync(Payload("901"), queueForDelivery: false);

        Assert.Equal("LocalOnly", ReadEventStatus(journal.DbPath, eventId));
        Assert.Empty(await journal.GetDueOutboxEventsAsync(20, DateTime.UtcNow.AddYears(1)));
        BridgeOutboxStatus outbox = await journal.GetOutboxStatusAsync("901", "SHOE901");
        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal(0, outbox.FailedCount);

        BridgeQueryPage<BridgeEventSummary> page = await journal.QueryEventsAsync(
            new BridgeStoredEventQuery(
                "901",
                "GameResult",
                "LocalOnly",
                null,
                null,
                202607260001,
                1,
                20,
                null,
                IncludePayload: true));
        BridgeEventSummary stored = Assert.Single(page.Items);
        Assert.Equal(eventId, stored.EventId);
        Assert.Equal("LocalOnly", stored.Status);
        Assert.NotNull(stored.PayloadJson);
    }

    [Theory]
    [InlineData("CardDrawn")]
    [InlineData("CutCardDrawn")]
    [InlineData("Error")]
    [InlineData("ErrorCleared")]
    [InlineData("LockStatus")]
    public async Task DiagnosticEvent_CannotEnterOutboxEvenWhenCallerRequestsDelivery(string type)
    {
        Directory.CreateDirectory(_directory);
        BridgeEventJournal journal = new(Path.Combine(_directory, $"{type}.sqlite"));
        Dictionary<string, object?> payload = Payload("901");
        payload["type"] = type;

        long eventId = await journal.AppendAsync(payload, queueForDelivery: true);

        Assert.Equal("LocalOnly", ReadEventStatus(journal.DbPath, eventId));
        Assert.Empty(await journal.GetDueOutboxEventsAsync(20, DateTime.UtcNow.AddYears(1)));
    }

    [Fact]
    public async Task CallerProvidedEventUid_IsPreservedAndConflictingPayloadIsRejected()
    {
        Directory.CreateDirectory(_directory);
        BridgeEventJournal journal = new(Path.Combine(_directory, "provided.sqlite"));
        Guid eventUid = Guid.NewGuid();
        Dictionary<string, object?> first = Payload("901");
        first["EventUid"] = eventUid;
        Dictionary<string, object?> duplicate = Payload("902");
        duplicate["eventUid"] = eventUid.ToString("D");

        long eventId = await journal.AppendAsync(first);
        await Assert.ThrowsAsync<InvalidDataException>(() => journal.AppendAsync(duplicate));

        Assert.Equal(eventUid.ToString("D"), ReadEventUid(journal.DbPath, eventId));
        Assert.False(first.ContainsKey("EventUid"));
        Assert.Equal(eventUid, Assert.IsType<Guid>(first["eventUid"]));
    }

    [Fact]
    public async Task CrashReplay_WithSameEventUidAndStablePayload_ReturnsExistingEvent()
    {
        Directory.CreateDirectory(_directory);
        BridgeEventJournal journal = new(Path.Combine(_directory, "crash-replay.sqlite"));
        Guid eventUid = Guid.NewGuid();
        Dictionary<string, object?> first = Payload("901");
        first["eventUid"] = eventUid;
        first["sequence"] = 10L;
        first["timestamp"] = "2026-07-26T01:00:00Z";
        Dictionary<string, object?> replay = Payload("901");
        replay["eventUid"] = eventUid;
        replay["sequence"] = 99L;
        replay["timestamp"] = "2026-07-26T01:05:00Z";

        long firstId = await journal.AppendAsync(first);
        long replayId = await journal.AppendAsync(replay);

        Assert.Equal(firstId, replayId);
        Assert.Equal(firstId, replay["eventId"]);
        using SqliteConnection connection = new($"Data Source={journal.DbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM bridge_events;";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public void Validator_RejectsColumnPayloadIdentityMismatch()
    {
        Guid columnUid = Guid.NewGuid();
        Guid payloadUid = Guid.NewGuid();
        BridgePendingEvent pending = new(
            1,
            "GameResult",
            "901",
            "SHOE901",
            202607260001,
            1,
            $$"""{"eventUid":"{{payloadUid:D}}"}""",
            0,
            columnUid.ToString("D"));

        bool valid = BridgeEventUidValidator.TryValidate(pending, out string error);

        Assert.False(valid);
        Assert.Contains("does not match", error);
    }

    [Fact]
    public void Validator_AcceptsMatchingDurableAndPayloadIdentity()
    {
        Guid eventUid = Guid.NewGuid();
        BridgePendingEvent pending = new(
            1,
            "GameResult",
            "901",
            "SHOE901",
            202607260001,
            1,
            $$"""{"eventUid":"{{eventUid:D}}"}""",
            0,
            eventUid.ToString("D"));

        bool valid = BridgeEventUidValidator.TryValidate(pending, out string error);

        Assert.True(valid);
        Assert.Empty(error);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static Dictionary<string, object?> Payload(string sourceDataCode) => new()
    {
        ["type"] = "GameResult",
        ["timestamp"] = "2026-07-26T08:00:00.0000000Z",
        ["source"] = "ANGEL",
        ["sourceDataCode"] = sourceDataCode,
        ["deviceId"] = $"SHOE{sourceDataCode}",
        ["shoe"] = 202607260001,
        ["round"] = 1,
        ["roundId"] = 1,
        ["data"] = new { result = "BankerWin" }
    };

    private static string ReadEventUid(string dbPath, long eventId)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT event_uid FROM bridge_events WHERE event_id = $event_id;";
        command.Parameters.AddWithValue("$event_id", eventId);
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static string ReadEventStatus(string dbPath, long eventId)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM bridge_events WHERE event_id = $event_id;";
        command.Parameters.AddWithValue("$event_id", eventId);
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static string ReadPayloadJson(string dbPath, long eventId)
    {
        using SqliteConnection connection = new($"Data Source={dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM bridge_events WHERE event_id = $event_id;";
        command.Parameters.AddWithValue("$event_id", eventId);
        return Assert.IsType<string>(command.ExecuteScalar());
    }
}
