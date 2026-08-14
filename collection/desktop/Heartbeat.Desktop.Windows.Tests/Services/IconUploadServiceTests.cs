using Heartbeat.Desktop.Windows.Services;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Collection.Hub.Http;
using System.Net;
using System.Text.Json;

namespace Heartbeat.Desktop.Windows.Tests.Services;

public class IconUploadServiceTests
{
    private sealed class FakeExtractor : IAppIconExtractor
    {
        public List<string> Calls { get; } = [];
        public byte[]? Extract(string appIdentityKey)
        {
            Calls.Add(appIdentityKey);
            return [1, 2, 3];
        }
    }

    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.UpgradeRequired ? "update now" : "")
            };
        }
    }

    [Fact]
    public async Task Upload_UsesIdentityAndDisplayName_WithoutLegacyAppName()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK);
        var extractor = new FakeExtractor();
        var compatibility = new ClientCompatibilityStatus();
        var service = new IconUploadService(
            new HeartbeatApiClient(new HttpClient(handler)), extractor, compatibility);

        await service.EnsureIconUploadedAsync("WIN:Code.exe", "Visual Studio Code");

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HeartbeatProtocol.RequiredVersion,
            call.Request.Headers.GetValues(HeartbeatProtocol.VersionHeader).Single());
        using var document = JsonDocument.Parse(call.Body);
        Assert.Equal("win:code", document.RootElement.GetProperty("appIdentityKey").GetString());
        Assert.Equal("Visual Studio Code", document.RootElement.GetProperty("appDisplayName").GetString());
        Assert.False(document.RootElement.TryGetProperty("appName", out _));
    }

    [Fact]
    public async Task UpgradeRequired_IsGlobalAndSuppressesRepeatedIconRequests()
    {
        var handler = new CapturingHandler(HttpStatusCode.UpgradeRequired);
        var compatibility = new ClientCompatibilityStatus();
        var service = new IconUploadService(
            new HeartbeatApiClient(new HttpClient(handler)), new FakeExtractor(), compatibility);

        await service.EnsureIconUploadedAsync("win:code", "Code");
        await service.EnsureIconUploadedAsync("win:mpv", "mpv");

        Assert.True(compatibility.Current.UpdateRequired);
        Assert.Contains("update now", compatibility.Current.Message);
        Assert.Single(handler.Calls);
    }
}
