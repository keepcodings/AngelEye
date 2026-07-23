using AngelEyeBmsBridge;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class WorkerQueryDataTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "angel-eye-tests", Guid.NewGuid().ToString("N"));
    private readonly BridgeEventJournal _journal;
    private readonly WorkerHttpRouter _router;
    private const long Shoe = 202607230001;

    public WorkerQueryDataTests()
    {
        Directory.CreateDirectory(_directory);
        _journal = new BridgeEventJournal(Path.Combine(_directory, "query.sqlite"));
        SeedAsync().GetAwaiter().GetResult();
        _router = new WorkerHttpRouter(
            new WorkerQuerySource("telebet-29", "QA", "Primary"),
            _ => (200, "{\"ok\":true}"),
            () => new WorkerStatusData("bridge", "QA", "1", true, []),
            _journal);
    }

    [Fact]
    public async Task RoundsQuery_AppliesHalfOpenDateFilter_AndCursorDoesNotRepeatRows()
    {
        string target = "/api/v1/rounds?deskCode=901&fromUtc=2026-07-23T00%3A00%3A00Z&toUtc=2026-07-23T02%3A00%3A00Z&pageSize=1";
        WorkerHttpResponse first = await _router.RouteAsync("GET", target);

        Assert.Equal(200, first.Status);
        using JsonDocument firstJson = JsonDocument.Parse(first.Body);
        JsonElement firstRows = firstJson.RootElement.GetProperty("data");
        Assert.Equal(1, firstRows.GetArrayLength());
        long firstRound = firstRows[0].GetProperty("round").GetInt64();
        string cursor = firstJson.RootElement.GetProperty("nextCursor").GetString()!;

        WorkerHttpResponse second = await _router.RouteAsync("GET", target + "&cursor=" + Uri.EscapeDataString(cursor));
        Assert.Equal(200, second.Status);
        using JsonDocument secondJson = JsonDocument.Parse(second.Body);
        JsonElement secondRows = secondJson.RootElement.GetProperty("data");
        Assert.Equal(1, secondRows.GetArrayLength());
        Assert.NotEqual(firstRound, secondRows[0].GetProperty("round").GetInt64());
    }

    [Theory]
    [InlineData("/api/v1/rounds?pageSize=0")]
    [InlineData("/api/v1/rounds?pageSize=201")]
    [InlineData("/api/v1/rounds?fromUtc=2026-07-23T02%3A00%3A00Z&toUtc=2026-07-23T01%3A00%3A00Z")]
    [InlineData("/api/v1/rounds?cursor=not-a-cursor")]
    public async Task RoundsQuery_RejectsInvalidFilters(string target)
    {
        WorkerHttpResponse response = await _router.RouteAsync("GET", target);

        Assert.Equal(400, response.Status);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.Equal("invalid_query", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RoundDetail_ReturnsChronologicalTimeline_AndMissingRoundIs404()
    {
        WorkerHttpResponse response = await _router.RouteAsync("GET", $"/api/v1/rounds/901/{Shoe}/1");

        Assert.Equal(200, response.Status);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        JsonElement data = json.RootElement.GetProperty("data");
        Assert.Equal("Settled", data.GetProperty("round").GetProperty("state").GetString());
        JsonElement timeline = data.GetProperty("timeline");
        Assert.Equal(3, timeline.GetArrayLength());
        Assert.Equal("StartGame", timeline[0].GetProperty("type").GetString());
        Assert.Equal("CardDrawn", timeline[1].GetProperty("type").GetString());
        Assert.Equal("GameResult", timeline[2].GetProperty("type").GetString());

        WorkerHttpResponse missing = await _router.RouteAsync("GET", $"/api/v1/rounds/901/{Shoe}/999");
        Assert.Equal(404, missing.Status);
        using JsonDocument missingJson = JsonDocument.Parse(missing.Body);
        Assert.Equal("round_not_found", missingJson.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task EventsQuery_RequiresPayloadOptIn_AndRedactsSecrets()
    {
        WorkerHttpResponse summaries = await _router.RouteAsync("GET", "/api/v1/events?deskCode=901&round=1");
        using JsonDocument summaryJson = JsonDocument.Parse(summaries.Body);
        Assert.All(summaryJson.RootElement.GetProperty("data").EnumerateArray(), item =>
            Assert.False(item.TryGetProperty("payloadJson", out _)));

        WorkerHttpResponse detailed = await _router.RouteAsync("GET", "/api/v1/events?deskCode=901&round=1&type=CardDrawn&includePayload=true");
        Assert.Equal(200, detailed.Status);
        using JsonDocument detailJson = JsonDocument.Parse(detailed.Body);
        string payload = detailJson.RootElement.GetProperty("data")[0].GetProperty("payloadJson").GetString()!;
        Assert.DoesNotContain("top-secret", payload, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutboxAndRecoveryQueries_ReturnFailureAndBackoffEvidence()
    {
        WorkerHttpResponse outbox = await _router.RouteAsync("GET", "/api/v1/outbox?deskCode=901&status=Failed");
        Assert.Equal(200, outbox.Status);
        using JsonDocument outboxJson = JsonDocument.Parse(outbox.Body);
        JsonElement failed = outboxJson.RootElement.GetProperty("data")[0];
        Assert.Equal(503, failed.GetProperty("httpStatus").GetInt32());
        Assert.Equal(1, failed.GetProperty("retryCount").GetInt32());

        WorkerHttpResponse recoveries = await _router.RouteAsync("GET", "/api/v1/recoveries?deskCode=901&result=Backoff");
        Assert.Equal(200, recoveries.Status);
        using JsonDocument recoveryJson = JsonDocument.Parse(recoveries.Body);
        JsonElement recovery = recoveryJson.RootElement.GetProperty("data")[0];
        Assert.Equal("recover-2", recovery.GetProperty("commandId").GetString());
        Assert.Equal("Backoff", recovery.GetProperty("result").GetString());
        Assert.True(recovery.TryGetProperty("nextRetryUtc", out _));
    }

    [Fact]
    public async Task ValidEmptyQuery_ReturnsEmptyCollectionAndNullCursor()
    {
        WorkerHttpResponse response = await _router.RouteAsync("GET", "/api/v1/rounds?deskCode=999");

        Assert.Equal(200, response.Status);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.Empty(json.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("nextCursor").ValueKind);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task SeedAsync()
    {
        await AddRoundAsync(1, "2026-07-23T00:00:00Z", settled: true, includeSecret: true);
        await AddRoundAsync(2, "2026-07-23T01:00:00Z", settled: false, includeSecret: false);
        long failedResult = await AddRoundAsync(3, "2026-07-23T02:00:00Z", settled: true, includeSecret: false);
        await _journal.MarkFailedAsync(failedResult, 1, new DateTime(2026, 7, 23, 2, 0, 20, DateTimeKind.Utc), "503 unavailable", 503);
        await _journal.RecordRecoveryRequestAsync(new BridgeRecoveryAudit(
            "recover-2", "RecoverRound", "901", "901", Shoe, 2, 2,
            new DateTimeOffset(2026, 7, 23, 1, 5, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 23, 1, 5, 0, TimeSpan.Zero),
            "Backoff", new DateTimeOffset(2026, 7, 23, 1, 6, 0, TimeSpan.Zero), "missing result"));
    }

    private async Task<long> AddRoundAsync(int round, string start, bool settled, bool includeSecret)
    {
        await _journal.AppendAsync(Payload("StartGame", round, start, new { startTime = start }));
        Dictionary<string, object?> card = Payload("CardDrawn", round, DateTimeOffset.Parse(start).AddSeconds(2).ToString("o"), new { target = "Player", index = 1, suit = "Spade", value = "8" });
        if (includeSecret)
        {
            card["jwtSigningKey"] = "top-secret";
        }
        await _journal.AppendAsync(card);
        if (!settled)
        {
            return 0;
        }
        return await _journal.AppendAsync(Payload("GameResult", round, DateTimeOffset.Parse(start).AddSeconds(10).ToString("o"), new { result = "Player", pair = "None" }));
    }

    private static Dictionary<string, object?> Payload(string type, int round, string timestamp, object data) => new()
    {
        ["type"] = type,
        ["source"] = "ANGEL",
        ["timestamp"] = timestamp,
        ["sourceDataCode"] = "901",
        ["deviceId"] = "901",
        ["shoe"] = Shoe,
        ["round"] = round,
        ["roundId"] = round,
        ["data"] = data
    };
}
