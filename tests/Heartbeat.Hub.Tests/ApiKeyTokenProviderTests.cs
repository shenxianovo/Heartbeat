using System.Net;
using System.Net.Http.Json;
using System.Text;
using Heartbeat.Hub;

namespace Heartbeat.Hub.Tests;

public sealed class ApiKeyTokenProviderTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-09-14T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task TokenIsCachedUntilItsRefreshWindow()
    {
        var owner = Guid.NewGuid();
        var time = new MutableTimeProvider(Now);
        var exchanges = 0;
        using var client = new HttpClient(new Handler(async (request, cancellationToken) =>
        {
            Assert.Equal("/api/v1/apikeys/exchange", request.RequestUri!.AbsolutePath);
            Assert.Equal(new ExchangeRequest("test-api-key"),
                await request.Content!.ReadFromJsonAsync<ExchangeRequest>(cancellationToken));
            exchanges++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    accessToken = QueueFixture.TokenFor(owner, Now.AddMinutes(exchanges == 1 ? 2 : 4)),
                    expiresIn = 120,
                }),
            };
        }));
        var provider = new ApiKeyTokenProvider(client, new Uri("https://auth.example/"), "test-api-key", time);

        var first = await provider.GetTokenAsync(cancellationToken: TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Same(first, await provider.GetTokenAsync(cancellationToken: TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromSeconds(2));
        var refreshed = await provider.GetTokenAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, exchanges);
        Assert.NotSame(first, refreshed);
        Assert.Equal(owner, refreshed!.OwnerId);
    }

    [Fact]
    public async Task ConcurrentCallersShareOneExchange()
    {
        var owner = Guid.NewGuid();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exchanges = 0;
        using var client = new HttpClient(new Handler(async (_, cancellationToken) =>
        {
            exchanges++;
            await release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    accessToken = QueueFixture.TokenFor(owner, Now.AddMinutes(10)),
                    expiresIn = 600,
                }),
            };
        }));
        var provider = new ApiKeyTokenProvider(client, new Uri("https://auth.example/"), "test-api-key",
            new MutableTimeProvider(Now));

        var calls = Enumerable.Range(0, 8).Select(_ => provider.GetTokenAsync().AsTask()).ToArray();
        release.SetResult();
        await Task.WhenAll(calls);

        Assert.Equal(1, exchanges);
        Assert.All(calls, call => Assert.Equal(owner, call.Result!.OwnerId));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"sub\":\"00000000-0000-0000-0000-000000000001\",\"exp\":{}}")]
    public async Task InvalidJwtPayloadIsAnUnconfirmedExchange(string payload)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    accessToken = $"header.{encoded}.signature",
                    expiresIn = 3600,
                }),
            })));
        var provider = new ApiKeyTokenProvider(client, new Uri("https://auth.example/"), "test-api-key");

        Assert.Null(await provider.GetTokenAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectedExchangeReportsStatusWithoutEchoingResponseOrCredentials()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("secret-api-key must not reach diagnostics"),
            })));
        using var provider = new ApiKeyTokenProvider(client, new Uri("https://auth.example/"), "secret-api-key");

        Assert.Null(await provider.GetTokenAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("403", provider.LastError);
        Assert.DoesNotContain("secret-api-key", provider.LastError);
    }

    [Fact]
    public async Task HttpTimeoutIsAnUnconfirmedExchangeWhenTheCallerDidNotCancel()
    {
        using var client = new HttpClient(new Handler((_, _) =>
            Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout"))));
        var provider = new ApiKeyTokenProvider(client, new Uri("https://auth.example/"), "test-api-key");

        Assert.Null(await provider.GetTokenAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    private sealed record ExchangeRequest(string ApiKey);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
