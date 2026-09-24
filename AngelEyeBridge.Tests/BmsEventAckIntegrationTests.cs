using System.Net;
using System.Text;
using System.Text.Json;
using AngelEyeBmsBridge;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class BmsEventAckIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "angel-eye-tests",
        Guid.NewGuid().ToString("N"));
    private readonly string _dbPath;

    public BmsEventAckIntegrationTests()
    {
        Directory.CreateDirectory(_directory);
        _dbPath = Path.Combine(_directory, "event-ack.sqlite");
    }

    [Fact]
    public async Task SuccessfulHttpWithMismatchedAckIdentity_RemainsUnconfirmed()
    {
        BridgeEventJournal journal = new(_dbPath);
        long eventId = await journal.AppendAsync(StartGamePayload());
        BridgePendingEvent pending = Assert.Single(
            await journal.GetDueOutboxEventsAsync(10, DateTime.UtcNow));
        string correlationId = string.Empty;
        using BmsApiClient client = new(new DelegateHandler(request =>
        {
            correlationId = request.Headers
                .GetValues(BridgeDiagnosticFormatter.CorrelationHeaderName)
                .Single();
            return Task.FromResult(JsonResponse(new
            {
                errCode = 0,
                data = new
                {
                    accepted = true,
                    duplicate = false,
                    eventId = eventId + 1,
                    eventUid = pending.EventUid
                }
            }));
        }));

        int dispatched = await client.RunDispatchOnceAsync(
            new BmsApiSettings(
                "https://bms.test/api/source/angel/events",
                "short-lived-token"),
            journal,
            _ => true);

        Assert.Equal(1, dispatched);
        Assert.Equal("Unconfirmed", ReadStatus(eventId));
        Assert.Equal(pending.EventUid, correlationId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NewShoeNotification_DispatchesWithoutStartGame_OnlyOnce(bool success)
    {
        BridgeEventJournal journal = new(_dbPath);
        var payload = StartGamePayload();
        payload["type"] = "NewShoeConfirmed";
        payload["round"] = 0;
        payload["roundId"] = null;
        long eventId = await journal.AppendAsync(payload);
        var pending = Assert.Single(await journal.GetDueOutboxEventsAsync(10, DateTime.UtcNow));
        int requests = 0;
        using BmsApiClient client = new(new DelegateHandler(_ =>
        {
            requests++;
            return Task.FromResult(success ? JsonResponse(new
            {
                errCode = 0, data = new { accepted = true, duplicate = false, eventId, eventUid = pending.EventUid }
            }) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }));
        var settings = new BmsApiSettings("https://bms.test/api/source/angel/events", "test-token");
        Assert.Equal(1, await client.RunDispatchOnceAsync(settings, journal, _ => true));
        Assert.Equal(success ? "Sent" : "Unconfirmed", ReadStatus(eventId));
        // Restart keeps delivery evidence but never replays this notice.
        journal = new BridgeEventJournal(_dbPath);
        Assert.Equal(success ? "Sent" : "Unconfirmed", ReadStatus(eventId));
        Assert.Equal(0, await client.RunDispatchOnceAsync(settings, journal, _ => true));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task NewShoeRestart_QuarantinesPending_AndDoesNotPromoteOldLocalOnly()
    {
        BridgeEventJournal journal = new(_dbPath);
        var payload = StartGamePayload();
        payload["type"] = "NewShoeConfirmed";
        payload["round"] = 0;
        payload["roundId"] = null;
        long pendingId = await journal.AppendAsync(payload);
        payload.Remove("eventUid");
        long localId = await journal.AppendAsync(payload, queueForDelivery: false);
        journal = new BridgeEventJournal(_dbPath);
        Assert.Equal("Unconfirmed", ReadStatus(pendingId));
        Assert.Equal("LocalOnly", ReadStatus(localId));
        Assert.Empty(await journal.GetDueOutboxEventsAsync(10, DateTime.UtcNow));
    }

    [Fact]
    public async Task CachedToken401_InvalidatesAndRetriesExactlyOnceBeforeAccepting()
    {
        BridgeEventJournal journal = new(_dbPath);
        long eventId = await journal.AppendAsync(StartGamePayload());
        BridgePendingEvent pending = Assert.Single(
            await journal.GetDueOutboxEventsAsync(10, DateTime.UtcNow));
        var observedTokens = new List<string>();
        int requestCount = 0;
        using BmsApiClient client = new(new DelegateHandler(request =>
        {
            observedTokens.Add(request.Headers.Authorization?.Parameter ?? string.Empty);
            int attempt = Interlocked.Increment(ref requestCount);
            return Task.FromResult(attempt == 1
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : JsonResponse(new
                {
                    errCode = 0,
                    data = new
                    {
                        accepted = true,
                        duplicate = false,
                        eventId,
                        eventUid = pending.EventUid
                    }
                }));
        }));
        using SequenceTokenProvider provider = new();

        int dispatched = await client.RunDispatchOnceAsync(
            new BmsApiSettings(
                "https://bms.test/api/source/angel/events",
                string.Empty),
            journal,
            _ => true,
            accessTokenProvider: provider);

        Assert.Equal(1, dispatched);
        Assert.Equal(2, requestCount);
        Assert.Equal(["token-1", "token-2"], observedTokens);
        Assert.Equal(["token-1"], provider.InvalidatedTokens);
        Assert.Equal("Sent", ReadStatus(eventId));
    }

    [Theory]
    [InlineData("http://bms.test/api/source/angel/events")]
    [InlineData("bms.test/api/source/angel/events")]
    public void DispatcherStart_RejectsNonHttpsUrlBeforeAnyRequest(string url)
    {
        BridgeEventJournal journal = new(_dbPath);
        using BmsApiClient client = new(new DelegateHandler(_ =>
            throw new Xunit.Sdk.XunitException("HTTP handler must not be called.")));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => client.Start(new BmsApiSettings(url, "token"), journal));

        Assert.Contains("HTTPS", exception.Message, StringComparison.Ordinal);
        Assert.False(client.IsRunning);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string ReadStatus(long eventId)
    {
        using SqliteConnection connection = new($"Data Source={_dbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM bridge_events WHERE event_id = $event_id;";
        command.Parameters.AddWithValue("$event_id", eventId);
        return Assert.IsType<string>(command.ExecuteScalar());
    }

    private static Dictionary<string, object?> StartGamePayload() => new()
    {
        ["type"] = "StartGame",
        ["timestamp"] = "2026-07-26T08:00:00.0000000Z",
        ["source"] = "ANGEL",
        ["sourceDataCode"] = "901",
        ["deviceId"] = "SHOE901",
        ["shoe"] = 202607260001,
        ["round"] = 1,
        ["roundId"] = 1,
        ["data"] = new { }
    };

    private static HttpResponseMessage JsonResponse(object payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return handler(request);
        }
    }

    private sealed class SequenceTokenProvider : IBmsAccessTokenProvider, IDisposable
    {
        private int _sequence;

        public List<string> InvalidatedTokens { get; } = [];

        public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                $"token-{Interlocked.Increment(ref _sequence)}");
        }

        public void InvalidateAccessToken(string rejectedAccessToken)
        {
            InvalidatedTokens.Add(rejectedAccessToken);
        }

        public void Dispose()
        {
        }
    }
}
