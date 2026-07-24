using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Http;
using Heartbeat.Agent.Models;
using Heartbeat.Agent.Services;
using System.Net;

namespace Heartbeat.Agent.Tests.Services;

/// <summary>
/// 声明上行（ADR-030 §3）：system 常量 + registry 声明推服务端;成功记 acked 不重发,
/// 失败整批留待下轮;上行失败不抛（不阻塞段上传节律）。
/// </summary>
public class DeclarationUplinkServiceTests : IDisposable
{
    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.NoContent;
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(Status);
        }
    }

    private readonly string _tempConfig = Path.Combine(Path.GetTempPath(), $"heartbeat-cfg-{Guid.NewGuid()}.json");
    private readonly ConfigManager _config;
    private readonly StubHandler _http = new();
    private readonly DeclarationUplinkService _uplink;

    public DeclarationUplinkServiceTests()
    {
        _config = new ConfigManager(_tempConfig);
        _uplink = new DeclarationUplinkService(new HeartbeatApiClient(new HttpClient(_http)), _config);
    }

    public void Dispose()
    {
        if (File.Exists(_tempConfig)) File.Delete(_tempConfig);
    }

    private const string BrowserDeclaration =
        """{"source":"browser","version":2,"layers":[{"readings":[{"name":"site","from":"attributes.site"}]}]}""";

    [Fact]
    public async Task Push_SendsSystemAndRegistryDeclarations_ThenAcks()
    {
        _config.Update(c => c.Collectors["browser"] = new CollectorEntry
        {
            DeclarationJson = BrowserDeclaration,
            DeclarationVersion = 2,
        });

        await _uplink.PushOnceAsync();
        await _uplink.PushOnceAsync(); // acked 后不再发

        var body = Assert.Single(_http.Bodies);
        Assert.Contains("\"source\":\"system\"", body);
        Assert.Contains("\"source\":\"browser\"", body);
        Assert.StartsWith("[", body);
    }

    [Fact]
    public async Task Push_Failure_RetriesNextRound()
    {
        _http.Status = HttpStatusCode.InternalServerError;
        await _uplink.PushOnceAsync();

        _http.Status = HttpStatusCode.NoContent;
        await _uplink.PushOnceAsync();

        Assert.Equal(2, _http.Bodies.Count); // 失败不 ack,下轮重发
    }

    [Fact]
    public async Task Push_NewDeclarationVersion_TriggersResend()
    {
        await _uplink.PushOnceAsync(); // system acked

        _config.Update(c => c.Collectors["browser"] = new CollectorEntry
        {
            DeclarationJson = BrowserDeclaration,
            DeclarationVersion = 2,
        });
        await _uplink.PushOnceAsync();

        Assert.Equal(2, _http.Bodies.Count);
        Assert.DoesNotContain("\"source\":\"system\"", _http.Bodies[1]); // 已 ack 的不重发
        Assert.Contains("\"source\":\"browser\"", _http.Bodies[1]);
    }
}
