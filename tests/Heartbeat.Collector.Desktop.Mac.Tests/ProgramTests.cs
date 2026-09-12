using System.Net;
using System.Net.Http.Json;
using Heartbeat.Collector.Desktop.Mac;
using Heartbeat.Hub;

namespace Heartbeat.Collector.Desktop.Mac.Tests;

public sealed class ProgramTests
{
    [Fact]
    public async Task ProgramStartsBySubmittingARecordDeclarationToHub()
    {
        using var handler = new CaptureHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:4318") };
        var options = new CollectorOptions(httpClient.BaseAddress, "local-token", "device-a", "Test Mac",
            TimeSpan.FromSeconds(5), true);

        var result = await Program.RunAsync(options, new HubSubmissionClient(httpClient), CancellationToken.None,
            new FixedReader());

        Assert.Equal(0, result);
        Assert.Equal("/hub/v1/records", handler.Path);
        Assert.Equal("heartbeat.collector.desktop.macos", handler.Submission!.Collector!.Key);
        Assert.Equal("desktop.application.foreground", handler.Submission.Track!.Type);
        Assert.Equal("range", handler.Submission.Track.TimeMode);
        Assert.Equal("explicit", handler.Submission.Track.EndMode);
    }

    [Fact]
    public async Task UnconfirmedCustodyReturnsFailure()
    {
        using var handler = new FailureHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:4318") };
        var options = new CollectorOptions(httpClient.BaseAddress, "local-token", "device-a", "Test Mac",
            TimeSpan.FromSeconds(5), true);

        var result = await Program.RunAsync(options, new HubSubmissionClient(httpClient), CancellationToken.None,
            new FixedReader());

        Assert.Equal(1, result);
        Assert.Equal(1, handler.RequestCount);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public HubSubmission? Submission { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Path = request.RequestUri!.AbsolutePath;
            Submission = await request.Content!.ReadFromJsonAsync<HubSubmission>(token);
            var record = Assert.Single(Submission!.Records!)!;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    results = new[] { new { index = 0, record.Id, status = "accepted", record.EndedAt } },
                }),
            };
        }
    }

    private sealed class FailureHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            RequestCount++;
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("Hub unavailable"));
        }
    }

    private sealed class FixedReader : IForegroundApplicationReader
    {
        public ForegroundApplication Read() => new("macos", "bundle_id", "com.apple.finder");
    }
}
