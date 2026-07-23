using AngelEyeBmsBridge;
using System.Net;
using System.Text;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class QueryConsoleClientTests
{
    [Fact]
    public void ModeSelector_DefaultsToQuery_AndRequiresExplicitEngineeringFlag()
    {
        Assert.Equal(ApplicationMode.Query, ApplicationModeSelector.Select([]));
        Assert.Equal(ApplicationMode.Query, ApplicationModeSelector.Select(["--query"]));
        Assert.Equal(ApplicationMode.Engineering, ApplicationModeSelector.Select(["--engineering"]));
        Assert.Equal(ApplicationMode.Deployment, ApplicationModeSelector.Select(["--deployment"]));
        Assert.Equal(ApplicationMode.Deployment, ApplicationModeSelector.Select(["--DEPLOYMENT"]));
        Assert.False(ApplicationModeSelector.IsEngineering([]));
        Assert.False(ApplicationModeSelector.IsEngineering(["--query"]));
        Assert.True(ApplicationModeSelector.IsEngineering(["--engineering"]));
        Assert.True(ApplicationModeSelector.IsEngineering(["--ENGINEERING"]));
    }

    [Fact]
    public async Task Client_UsesOnlyGet_AndMapsRoundFilters()
    {
        RecordingHandler handler = new("""
            {"meta":{"apiVersion":"v1","instanceName":"telebet-29","environment":"QA","role":"Primary","generatedAtUtc":"2026-07-23T00:00:00Z"},"data":[],"nextCursor":null}
            """);
        using TeleBetQueryClient client = new(new HttpClient(handler));

        await client.GetRoundsAsync(
            new Uri("http://127.0.0.1:18080"),
            new QueryRoundFilters("901", new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero), null, 202607230001, 93, "Settled"));

        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        string query = handler.LastRequest.RequestUri!.Query;
        Assert.Contains("deskCode=901", query, StringComparison.Ordinal);
        Assert.Contains("shoe=202607230001", query, StringComparison.Ordinal);
        Assert.Contains("round=93", query, StringComparison.Ordinal);
        Assert.Contains("state=Settled", query, StringComparison.Ordinal);
    }

    [Fact]
    public void State_PreservesLastSuccessfulData_WhenOffline()
    {
        QueryConsoleState state = new();
        QueryStatusData status = new("bridge", "QA", "1", true, []);
        DateTimeOffset successAt = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
        state.MarkSuccess(status, successAt);

        state.MarkFailure("offline");

        Assert.Same(status, state.LastStatus);
        Assert.Equal(successAt, state.LastSuccessfulUtc);
        Assert.True(state.IsOffline);
        Assert.Equal("offline", state.LastError);
    }

    [Fact]
    public void State_FirstOffline_HasNoFabricatedStatus()
    {
        QueryConsoleState state = new();

        state.MarkFailure("tunnel unavailable");

        Assert.Null(state.LastStatus);
        Assert.Null(state.LastSuccessfulUtc);
        Assert.True(state.IsOffline);
    }

    [Fact]
    public void Projection_VerifiesProfileMetadata_AndMapsEndpointHealth()
    {
        QueryServerProfile profile = QueryServerProfile.Defaults[0];
        QueryMetadata verified = new("v1", "telebet-29", "QA", "Primary", DateTimeOffset.UtcNow);
        QueryMetadata wrongRole = verified with { Role = "Standby" };
        QueryEndpointData healthy = new("901桌", "901", "901", true, true, "Dealing", null, "", 1, 2, 2, true, 0, 0);
        QueryEndpointData offline = healthy with { Connected = false };

        Assert.True(QueryConsoleProjection.IsMetadataVerified(verified, profile));
        Assert.False(QueryConsoleProjection.IsMetadataVerified(wrongRole, profile));
        Assert.Equal("正常", QueryConsoleProjection.ConnectionState(healthy));
        Assert.Equal("中斷", QueryConsoleProjection.ConnectionState(offline));
        Assert.Equal("停用", QueryConsoleProjection.ConnectionState(offline with { Enabled = false }));
    }

    [Fact]
    public void Projection_MergesCursorPagesWithoutDuplicateRounds()
    {
        QueryRoundData first = Round(1, complete: true);
        QueryRoundData second = Round(2, complete: true);

        IReadOnlyList<QueryRoundData> merged = QueryConsoleProjection.MergeRounds([first], [first, second]);

        Assert.Equal([1L, 2L], merged.Select(item => item.Round));
    }

    [Fact]
    public void Projection_ClassifiesAndSortsAllSupportedExceptions()
    {
        DateTimeOffset now = new(2026, 7, 23, 2, 0, 0, TimeSpan.Zero);
        QueryRoundData incomplete = Round(1, complete: false) with { UpdatedUtc = "2026-07-23T01:00:00Z" };
        QueryEventData failed = Event(2, "Failed", "2026-07-23T01:30:00Z") with { LastError = "503", NextRetryUtc = "2026-07-23T01:31:00Z" };
        QueryEventData pending = Event(3, "Pending", "2026-07-23T01:00:00Z");
        QueryRecoveryData recovery = new("cmd-1", "RecoverRound", "901", "901", 10, 1, 1,
            "2026-07-23T01:40:00Z", "2026-07-23T01:41:00Z", "Backoff", "2026-07-23T01:42:00Z", "wait");

        IReadOnlyList<QueryExceptionItem> rows = QueryConsoleProjection.BuildExceptions(
            [incomplete], [failed], [pending], [recovery], now);

        Assert.Equal(4, rows.Count);
        Assert.Equal("RecoverRound Backoff", rows[0].Type);
        Assert.Contains(rows, row => row.Type == "缺 GameResult");
        Assert.Contains(rows, row => row.Type == "Outbox 失敗" && row.NextRetry != null);
        Assert.Contains(rows, row => row.Type == "Outbox 逾時待送");
    }

    [Fact]
    public void DisplayText_DoesNotClaimFinalBmsStorage_AndMarksIncompleteRound()
    {
        Assert.Equal("BMS endpoint accepted", QueryDisplayText.Delivery("Sent"));
        QueryRoundData round = new("901", "901", 202607230001, 93, 93, null, null, "Dealing", null, null, "2026-07-23T00:00:00Z", false, null, 0, null, null, null, null);
        Assert.Contains("本機無 GameResult", QueryDisplayText.RoundState(round), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_IgnoresUnknownSecretFields_InAllowListedDtos()
    {
        RecordingHandler handler = new("""
            {"meta":{"apiVersion":"v1","instanceName":"telebet-29","environment":"QA","role":"Primary","generatedAtUtc":"2026-07-23T00:00:00Z","signingKey":"secret"},"data":{"bridgeId":"bridge","bridgeName":"QA","version":"1","bmsDispatcherRunning":true,"authorization":"Bearer secret","endpoints":[]},"nextCursor":null}
            """);
        using TeleBetQueryClient client = new(new HttpClient(handler));

        QueryApiEnvelope<QueryStatusData> result = await client.GetStatusAsync(new Uri("http://127.0.0.1:18080"));

        Assert.Equal("telebet-29", result.Meta.InstanceName);
        Assert.Equal("bridge", result.Data.BridgeId);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    private static QueryRoundData Round(long round, bool complete) => new(
        "901", "901", 10, round, round, "2026-07-23T00:00:00Z", complete ? "2026-07-23T00:00:10Z" : null,
        complete ? "Settled" : "Dealing", "[]", complete ? "{}" : null, "2026-07-23T00:00:10Z",
        complete, complete ? "Sent" : null, 0, null, complete ? "2026-07-23T00:00:11Z" : null, null, complete ? 200 : null);

    private static QueryEventData Event(long eventId, string status, string occurredUtc) => new(
        eventId, occurredUtc, "GameResult", "901", "901", 10, eventId, eventId, status, 0, null, null, null, null, null, null);

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            });
        }
    }
}
