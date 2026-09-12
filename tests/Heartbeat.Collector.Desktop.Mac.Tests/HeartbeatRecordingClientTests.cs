using System.Net;
using System.Text.Json;
using Heartbeat.Collector.Desktop.Mac;

namespace Heartbeat.Collector.Desktop.Mac.Tests;

public sealed class HeartbeatRecordingClientTests
{
    [Fact]
    public async Task ClientRegistersResolvesAndUploadsForegroundRecord()
    {
        var handler = new ScriptedHandler(
            (HttpMethod.Post, "/api/v1/collectors", """{"id":"019e0000-0000-7000-8000-000000000001"}"""),
            (HttpMethod.Post, "/api/v1/collectors/019e0000-0000-7000-8000-000000000001/tracks", """{"id":"019e0000-0000-7000-8000-000000000002"}"""),
            (HttpMethod.Post, "/api/v1/tracks/019e0000-0000-7000-8000-000000000002/records", """{"results":[{"status":"stored"}]}"""));
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8080") };
        var client = new HeartbeatRecordingClient(httpClient);
        var startedAt = new DateTimeOffset(2026, 9, 12, 16, 0, 0, TimeSpan.Zero);
        var record = new ForegroundRecord(
            Guid.Parse("019e0000-0000-7000-8000-000000000003"),
            startedAt,
            startedAt.AddSeconds(5),
            new ForegroundApplication("macos", "bundle_id", "com.example.App"));

        var collectorId = await client.RegisterCollectorAsync("device-a", "My Mac");
        var trackId = await client.ResolveForegroundTrackAsync(collectorId);
        await client.UploadAsync(trackId, "device-a", record);

        Assert.Equal(Guid.Parse("019e0000-0000-7000-8000-000000000001"), collectorId);
        Assert.Equal(Guid.Parse("019e0000-0000-7000-8000-000000000002"), trackId);
        using var upload = JsonDocument.Parse(handler.RequestBodies[2]);
        var item = upload.RootElement.GetProperty("records")[0];
        Assert.Equal(record.Id, item.GetProperty("id").GetGuid());
        Assert.Equal("device-a", item.GetProperty("value").GetProperty("device_id").GetString());
        Assert.Equal("bundle_id", item.GetProperty("value").GetProperty("application").GetProperty("id_kind").GetString());
    }

    private sealed class ScriptedHandler(params (HttpMethod Method, string Path, string Response)[] script)
        : HttpMessageHandler
    {
        private int _index;

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var expected = script[_index++];
            Assert.Equal(expected.Method, request.Method);
            Assert.Equal(expected.Path, request.RequestUri?.PathAndQuery);
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(expected.Response, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
