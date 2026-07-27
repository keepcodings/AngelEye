using System.Net;
using System.Text;
using System.Text.Json;
using AngelEyeBmsBridge;
using Xunit;

namespace AngelEyeBridge.Tests;

public sealed class BmsClientCredentialsAccessTokenProviderTests
{
    private static readonly DateTimeOffset InitialNow =
        DateTimeOffset.Parse("2026-07-26T00:00:00Z");

    [Fact]
    public async Task FirstRequest_UsesExactSiblingTokenEndpointAndJsonContract_ThenCaches()
    {
        DateTimeOffset now = InitialNow;
        int requestCount = 0;
        string requestJson = string.Empty;
        Uri? requestedUri = null;
        using BmsClientCredentialsAccessTokenProvider provider = new(
            "https://bms.test/api/source/angel/events?ignored=true",
            "angel-qa-29",
            "super-secret",
            "QA-29",
            new DelegateHandler(async request =>
            {
                Interlocked.Increment(ref requestCount);
                requestedUri = request.RequestUri;
                requestJson = await request.Content!.ReadAsStringAsync();
                return TokenResponse("token-1", now.AddMinutes(5), 300, "QA-29");
            }),
            () => now);

        string first = await provider.GetAccessTokenAsync(CancellationToken.None);
        string cached = await provider.GetAccessTokenAsync(CancellationToken.None);

        Assert.Equal("token-1", first);
        Assert.Equal(first, cached);
        Assert.Equal(1, requestCount);
        Assert.NotNull(requestedUri);
        Assert.Equal("https", requestedUri.Scheme);
        Assert.Equal("/api/source/angel/token", requestedUri.AbsolutePath);
        Assert.Equal(string.Empty, requestedUri.Query);

        using JsonDocument request = JsonDocument.Parse(requestJson);
        Assert.Equal("angel-qa-29", request.RootElement.GetProperty("clientId").GetString());
        Assert.Equal("super-secret", request.RootElement.GetProperty("clientSecret").GetString());
        Assert.Equal(2, request.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task TokenIsRefreshedBeforeExpiry_AndConcurrentCallersShareRefresh()
    {
        DateTimeOffset now = InitialNow;
        int requestCount = 0;
        using BmsClientCredentialsAccessTokenProvider provider = new(
            "https://bms.test/api/source/angel/events",
            "angel-qa-29",
            "super-secret",
            "QA-29",
            new DelegateHandler(async _ =>
            {
                int sequence = Interlocked.Increment(ref requestCount);
                await Task.Delay(10);
                return TokenResponse(
                    $"token-{sequence}",
                    now.AddSeconds(100),
                    100,
                    "QA-29");
            }),
            () => now);

        Assert.Equal(
            "token-1",
            await provider.GetAccessTokenAsync(CancellationToken.None));

        now = now.AddSeconds(89);
        Assert.Equal(
            "token-1",
            await provider.GetAccessTokenAsync(CancellationToken.None));

        now = now.AddSeconds(2);
        string[] refreshed = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(async _ =>
                    await provider.GetAccessTokenAsync(CancellationToken.None)));

        Assert.All(refreshed, token => Assert.Equal("token-2", token));
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task RejectedCachedToken_IsInvalidatedWithoutClearingANewerToken()
    {
        DateTimeOffset now = InitialNow;
        int requestCount = 0;
        using BmsClientCredentialsAccessTokenProvider provider = new(
            "https://bms.test/api/source/angel/events",
            "angel-qa-29",
            "super-secret",
            "QA-29",
            new DelegateHandler(_ =>
            {
                int sequence = Interlocked.Increment(ref requestCount);
                return Task.FromResult(TokenResponse(
                    $"token-{sequence}",
                    now.AddMinutes(5),
                    300,
                    "QA-29"));
            }),
            () => now);

        string first = await provider.GetAccessTokenAsync(CancellationToken.None);
        provider.InvalidateAccessToken("another-token");
        Assert.Equal(first, await provider.GetAccessTokenAsync(CancellationToken.None));

        provider.InvalidateAccessToken(first);
        Assert.Equal("token-2", await provider.GetAccessTokenAsync(CancellationToken.None));
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task MismatchedBridgeIdentity_IsRejectedAndNeverCached()
    {
        DateTimeOffset now = InitialNow;
        int requestCount = 0;
        using BmsClientCredentialsAccessTokenProvider provider = new(
            "https://bms.test/api/source/angel/events",
            "angel-qa-29",
            "super-secret",
            "QA-29",
            new DelegateHandler(_ =>
            {
                Interlocked.Increment(ref requestCount);
                return Task.FromResult(
                    TokenResponse("wrong-bridge-token", now.AddMinutes(5), 300, "QA-30"));
            }),
            () => now);

        InvalidOperationException first = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await provider.GetAccessTokenAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await provider.GetAccessTokenAsync(CancellationToken.None));

        Assert.Contains("bridge identity", first.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task TokenEnvelopeWithoutExplicitSuccessCode_IsRejected()
    {
        DateTimeOffset now = InitialNow;
        using BmsClientCredentialsAccessTokenProvider provider = new(
            "https://bms.test/api/source/angel/events",
            "angel-qa-29",
            "super-secret",
            "QA-29",
            new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new
                    {
                        data = new
                        {
                            accessToken = "must-not-be-used",
                            tokenType = "Bearer",
                            expiresInSeconds = 300,
                            expiresAt = now.AddMinutes(5),
                            bridgeId = "QA-29"
                        }
                    }),
                    Encoding.UTF8,
                    "application/json")
            })),
            () => now);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await provider.GetAccessTokenAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("http://bms.test/api/source/angel/events")]
    [InlineData("bms.test/api/source/angel/events")]
    public void Constructor_RejectsNonHttpsEventsUrl(string eventApiUrl)
    {
        Assert.Throws<InvalidOperationException>(() =>
            new BmsClientCredentialsAccessTokenProvider(
                eventApiUrl,
                "client-id",
                "client-secret",
                "QA-29"));
    }

    [Fact]
    public void DefaultAuthenticatedTransport_DoesNotFollowRedirects()
    {
        using HttpClientHandler handler = BmsApiClient.CreateSecureHttpHandler();
        Assert.False(handler.AllowAutoRedirect);
    }

    private static HttpResponseMessage TokenResponse(
        string accessToken,
        DateTimeOffset expiresAt,
        int expiresInSeconds,
        string bridgeId) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                errCode = 0,
                data = new
                {
                    accessToken,
                    tokenType = "Bearer",
                    expiresInSeconds,
                    expiresAt,
                    bridgeId
                }
            }),
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
}
