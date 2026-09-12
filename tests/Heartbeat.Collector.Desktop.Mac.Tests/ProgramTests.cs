using System.Net;
using Heartbeat.Collector.Desktop.Mac;

namespace Heartbeat.Collector.Desktop.Mac.Tests;

public sealed class ProgramTests
{
    [Theory]
    [InlineData(0, false, 1)]
    [InlineData(1, false, 1)]
    [InlineData(0, true, 0)]
    [InlineData(1, true, 0)]
    public async Task OnlyUserCancellationExitsSuccessfully(int successfulRequests, bool userCancelled, int expectedExitCode)
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new CancelRequestHandler(successfulRequests, userCancelled ? cancellation.Cancel : null);
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new HeartbeatRecordingClient(httpClient);
        var options = new CollectorOptions(httpClient.BaseAddress, "test-token", "device-a", "Test Mac",
            TimeSpan.FromSeconds(5), true);

        var exitCode = await Program.RunAsync(options, client, cancellation.Token);

        Assert.Equal(expectedExitCode, exitCode);
        Assert.Equal(successfulRequests + 1, handler.RequestCount);
    }

    private sealed class CancelRequestHandler(int successfulRequests, Action? cancel) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (RequestCount++ < successfulRequests)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":"019e0000-0000-7000-8000-000000000001"}"""),
                });
            }

            if (cancel is not null)
            {
                cancel();
                return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
            }

            return Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("The HTTP request timed out.", new TimeoutException()));
        }
    }
}
