using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Http;
using Heartbeat.Agent.Models;

namespace Heartbeat.Agent.Tests.Http;

public class BearerTokenHandlerTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) File.Delete(f);
    }

    [Fact]
    public async Task SendAsync_DeviceNameEmpty_SendsMachineNameHeader()
    {
        var configManager = CreateConfigManager(new AgentConfig { DeviceName = "" });
        var capturingHandler = new CapturingHandler();
        var handler = new BearerTokenHandler(configManager, new FakeTokenProvider("jwt"))
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
        var configManager = CreateConfigManager(new AgentConfig { DeviceName = "我的电脑" });
        var capturingHandler = new CapturingHandler();
        var handler = new BearerTokenHandler(configManager, new FakeTokenProvider("jwt"))
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

    private ConfigManager CreateConfigManager(AgentConfig config)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"heartbeat-test-{Guid.NewGuid()}.json");
        _tempFiles.Add(tempPath);
        var cm = new ConfigManager(tempPath);
        cm.Update(c =>
        {
            c.ApiKey = config.ApiKey;
            c.DeviceName = config.DeviceName;
        });
        return cm;
    }

    private class FakeTokenProvider(string token) : IAccessTokenProvider
    {
        public Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
            => Task.FromResult<string?>(token);
        public void InvalidateToken() { }
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
