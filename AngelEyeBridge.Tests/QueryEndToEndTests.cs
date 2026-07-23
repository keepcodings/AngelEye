using AngelEyeBmsBridge;
using System.Net;
using System.Text;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class QueryEndToEndTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "angel-eye-tests", Guid.NewGuid().ToString("N"));
    private readonly BridgeEventJournal _journal;

    public QueryEndToEndTests()
    {
        Directory.CreateDirectory(_directory);
        _journal = new BridgeEventJournal(Path.Combine(_directory, "e2e.sqlite"));
    }

    [Fact]
    public async Task Journal_Router_AndGuiClient_PreserveOperationalEvidenceEndToEnd()
    {
        const long shoe = 202607232359;
        long resultId = await _journal.AppendAsync(Payload("StartGame", shoe, 1, "2026-07-23T23:59:58Z", new { }));
        await _journal.AppendAsync(Payload("CardDrawn", shoe, 1, "2026-07-23T23:59:59Z", new { target = "Player", value = "8" }));
        resultId = await _journal.AppendAsync(Payload("GameResult", shoe, 1, "2026-07-24T00:00:02Z", new { result = "Player" }));
        await _journal.MarkFailedAsync(resultId, 1, new DateTime(2026, 7, 24, 0, 0, 3, DateTimeKind.Utc), "503 retry", 503);
        await _journal.AppendAsync(Payload("StartGame", shoe, 2, "2026-07-24T00:01:00Z", new { }));
        await _journal.RecordRecoveryRequestAsync(new BridgeRecoveryAudit(
            "recover-e2e", "RecoverRound", "901", "901", shoe, 2, 2,
            DateTimeOffset.Parse("2026-07-24T00:02:00Z"), DateTimeOffset.Parse("2026-07-24T00:02:01Z"),
            "NotFound", DateTimeOffset.Parse("2026-07-24T00:03:00Z"), "missing local result"));

        WorkerHttpRouter router = new(
            new WorkerQuerySource("telebet-29", "QA", "Primary"),
            _ => (200, "{\"ok\":true}"),
            () => new WorkerStatusData("bridge", "QA", "1", true, []),
            _journal);
        RouterHandler handler = new(router);
        using TeleBetQueryClient client = new(new HttpClient(handler));
        Uri baseUri = new("http://127.0.0.1:18080");

        QueryApiEnvelope<List<QueryRoundData>> rounds = await client.GetRoundsAsync(
            baseUri, new QueryRoundFilters("901", DateTimeOffset.Parse("2026-07-23T23:00:00Z"), DateTimeOffset.Parse("2026-07-24T01:00:00Z"), null, null, ""));
        QueryRoundData settled = Assert.Single(rounds.Data, item => item.Round == 1);
        QueryRoundData incomplete = Assert.Single(rounds.Data, item => item.Round == 2);
        Assert.True(settled.IsComplete);
        Assert.Equal("Failed", settled.DeliveryStatus);
        Assert.False(incomplete.IsComplete);

        QueryApiEnvelope<QueryRoundDetailData> detail = await client.GetRoundDetailAsync(baseUri, "901", shoe, 1);
        Assert.Equal(["StartGame", "CardDrawn", "GameResult"], detail.Data.Timeline.Select(item => item.Type));
        Assert.True(DateTimeOffset.Parse(detail.Data.Timeline[0].OccurredUtc) < DateTimeOffset.Parse(detail.Data.Timeline[^1].OccurredUtc));

        QueryApiEnvelope<List<QueryRecoveryData>> recoveries = await client.GetRecoveriesAsync(baseUri, result: "NotFound");
        Assert.Equal("recover-e2e", Assert.Single(recoveries.Data).CommandId);
        Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

    private sealed class RouterHandler(WorkerHttpRouter router) : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            WorkerHttpResponse response = await router.RouteAsync(
                request.Method.Method,
                request.RequestUri!.PathAndQuery,
                cancellationToken);
            return new HttpResponseMessage((HttpStatusCode)response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json")
            };
        }
    }
}
