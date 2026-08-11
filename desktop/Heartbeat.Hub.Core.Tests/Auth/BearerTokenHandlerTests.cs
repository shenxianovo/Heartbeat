using Heartbeat.Hub.Core.Auth;
using Heartbeat.Hub.Core.Configuration;

namespace Heartbeat.Hub.Core.Tests.Auth;

public class BearerTokenHandlerTests
{
    [Fact]
    public async Task SendAsync_DeviceNameEmpty_SendsMachineNameHeader()
    {
        var capturingHandler = new CapturingHandler();
        var handler = new BearerTokenHandler(new FakeTokenProvider("jwt"), new FakeDeviceIdentity(Environment.MachineName))
        {
            InnerHandler = capturingHandler
        };

        var client = new HttpClient(handler);
        await client.GetAsync("http://localhost/test");

        var header = capturingHandler.CapturedRequest!.Headers
            .GetValues("X-Device-Name").FirstOrDefault();

        Assert.NotNull(header);
        Assert.Equal(Environment.MachineName, Uri.UnescapeDataString(header));
    }

    [Fact]
    public async Task SendAsync_DeviceNameSet_SendsConfiguredName()
    {
        var capturingHandler = new CapturingHandler();
        var handler = new BearerTokenHandler(new FakeTokenProvider("jwt"), new FakeDeviceIdentity("我的电脑"))
        {
            InnerHandler = capturingHandler
        };

        var client = new HttpClient(handler);
        await client.GetAsync("http://localhost/test");

        var header = capturingHandler.CapturedRequest!.Headers
            .GetValues("X-Device-Name").FirstOrDefault();

        Assert.NotNull(header);
        Assert.Equal("我的电脑", Uri.UnescapeDataString(header));
    }

    private class FakeTokenProvider(string token) : IAccessTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
            => Task.FromResult<string?>(token);
        public void InvalidateToken() { }
    }

    private sealed class FakeDeviceIdentity(string deviceName) : IDeviceIdentity
    {
        public string HardwareId => "test-machine-guid";
        public string DeviceName => deviceName;
    }

    private class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
