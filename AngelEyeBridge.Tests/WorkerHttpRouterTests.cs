using AngelEyeBmsBridge;
using System.Text.Json;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class WorkerHttpRouterTests
{
    private readonly WorkerHttpRouter _router = new(
        new WorkerQuerySource("telebet-29", "QA", "Primary"),
        _ => (503, "{\"ok\":false,\"legacy\":true}"),
        () => new WorkerStatusData(
            "bridge-29",
            "Midori QA",
            "1.0.0",
            true,
            [new WorkerEndpointStatusData("901桌", "901", "SHOE901", true, true, "已連線", null, "", 202607230001, 1, 1, true, 0, 0)]));

    [Fact]
    public async Task HealthRoute_PreservesLegacyStatusAndBody()
    {
        WorkerHttpResponse response = await _router.RouteAsync("GET", "/health");

        Assert.Equal(503, response.Status);
        Assert.Equal("Service Unavailable", response.ReasonPhrase);
        Assert.Equal("{\"ok\":false,\"legacy\":true}", response.Body);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task QueryApi_RejectsMutationMethods(string method)
    {
        WorkerHttpResponse response = await _router.RouteAsync(method, "/api/v1/status");

        Assert.Equal(405, response.Status);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.Equal("method_not_allowed", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task StatusRoute_ReturnsMetadataAndAllowListedEndpointData()
    {
        WorkerHttpResponse response = await _router.RouteAsync("GET", "/api/v1/status");

        Assert.Equal(200, response.Status);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        JsonElement meta = json.RootElement.GetProperty("meta");
        Assert.Equal("v1", meta.GetProperty("apiVersion").GetString());
        Assert.Equal("telebet-29", meta.GetProperty("instanceName").GetString());
        Assert.Equal("QA", meta.GetProperty("environment").GetString());
        Assert.Equal("Primary", meta.GetProperty("role").GetString());
        JsonElement endpoint = json.RootElement.GetProperty("data").GetProperty("endpoints")[0];
        Assert.Equal("901", endpoint.GetProperty("sourceDataCode").GetString());
        Assert.Equal(0, endpoint.GetProperty("pendingCount").GetInt32());
        Assert.False(endpoint.TryGetProperty("jwtSigningKey", out _));
    }

    [Fact]
    public async Task UnknownRoute_ReturnsConsistent404Envelope()
    {
        WorkerHttpResponse response = await _router.RouteAsync("GET", "/api/v1/unknown");

        Assert.Equal(404, response.Status);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.Equal("route_not_found", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnexpectedFailure_ReturnsGeneric500WithoutStackOrSecret()
    {
        WorkerHttpRouter failing = new(
            new WorkerQuerySource("telebet-29", "QA", "Primary"),
            _ => throw new InvalidOperationException("Authorization: Bearer secret-value"),
            () => throw new InvalidOperationException("SigningKey=secret-value"));

        WorkerHttpResponse response = await failing.RouteAsync("GET", "/api/v1/status");

        Assert.Equal(500, response.Status);
        Assert.DoesNotContain("secret-value", response.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", response.Body, StringComparison.Ordinal);
        using JsonDocument json = JsonDocument.Parse(response.Body);
        Assert.Equal("internal_error", json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
