using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Services;
using System.Text;

namespace Heartbeat.Agent.Tests.Services;

/// <summary>
/// loopback ingest 协议契约（ADR-020/026）：插件作者看到的状态码、错误体、accepted 计数，
/// 以及采集器发现（GET config 自动注册）与强制层停用（disabled → 403）。
/// </summary>
public class SegmentIngestRequestHandlerTests : IDisposable
{
    private sealed class FakeClock : Heartbeat.Agent.Utils.IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    private readonly string _tempConfig = Path.Combine(Path.GetTempPath(), $"heartbeat-cfg-{Guid.NewGuid()}.json");
    private readonly ConfigManager _config;
    private readonly SegmentIngestService _ingest;
    private readonly SegmentIngestRequestHandler _handler;

    public SegmentIngestRequestHandlerTests()
    {
        _config = new ConfigManager(_tempConfig);
        _ingest = new SegmentIngestService(new FakeClock());
        _handler = new SegmentIngestRequestHandler(_ingest, _config);
    }

    public void Dispose()
    {
        if (File.Exists(_tempConfig)) File.Delete(_tempConfig);
    }

    private static Stream Body(string json) => new MemoryStream(Encoding.UTF8.GetBytes(json));

    private static string SegmentJson(string source = "browser", string identityKey = "https://example.com")
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        return $$"""
            {
              "id": "{{Guid.CreateVersion7()}}",
              "source": "{{source}}",
              "identityKey": "{{identityKey}}",
              "startTime": "{{start:O}}",
              "endTime": "{{start.AddMinutes(2):O}}"
            }
            """;
    }

    [Fact]
    public async Task WrongPath_404()
    {
        var response = await _handler.HandleAsync("POST", "/v1/other", Body("{}"));

        Assert.Equal(404, response.StatusCode);
        Assert.Contains("POST /v1/segments", response.Body);
    }

    [Fact]
    public async Task HubIdentity_200_HeartbeatJson()
    {
        // 采集器凭此在端口范围内识别 hub（陌生服务不会这样应答）。
        var response = await _handler.HandleAsync("GET", "/v1/hub", Body(""));

        Assert.Equal(200, response.StatusCode);
        Assert.True(response.IsJson);
        Assert.Equal("""{"app":"heartbeat","proto":1}""", response.Body);
    }

    [Fact]
    public async Task HubIdentity_WrongMethod_404()
    {
        var response = await _handler.HandleAsync("POST", "/v1/hub", Body("{}"));

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task WrongMethod_404()
    {
        var response = await _handler.HandleAsync("GET", "/v1/segments", Body("{}"));

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task InvalidJson_400()
    {
        var response = await _handler.HandleAsync("POST", "/v1/segments", Body("{not json"));

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("invalid JSON", response.Body);
    }

    [Fact]
    public async Task EmptySegments_400()
    {
        var missing = await _handler.HandleAsync("POST", "/v1/segments", Body("{}"));
        var empty = await _handler.HandleAsync("POST", "/v1/segments", Body("""{"segments":[]}"""));

        Assert.Equal(400, missing.StatusCode);
        Assert.Equal(400, empty.StatusCode);
        Assert.Equal("segments cannot be empty", empty.Body);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("System")]
    public async Task SystemSource_400_NothingBuffered(string source)
    {
        // 冒充守卫：loopback 来的 'system' 段整批拒收，缓冲不留痕。
        var json = $$"""{"segments":[{{SegmentJson()}},{{SegmentJson(source: source)}}]}""";

        var response = await _handler.HandleAsync("POST", "/v1/segments", Body(json));

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("reserved", response.Body);
        Assert.Empty(_ingest.GetAndClearSegments());
    }

    [Fact]
    public async Task ValidBatch_200_AcceptedCount()
    {
        var json = $$"""{"segments":[{{SegmentJson()}},{{SegmentJson(identityKey: "https://other.com")}}]}""";

        var response = await _handler.HandleAsync("POST", "/v1/segments", Body(json));

        Assert.Equal(200, response.StatusCode);
        Assert.True(response.IsJson);
        Assert.Equal("""{"accepted":2}""", response.Body);
        Assert.Equal(2, _ingest.GetAndClearSegments().Count);
    }

    [Fact]
    public async Task InvalidSegmentsFiltered_200_AcceptedZero()
    {
        // 校验丢弃不是错误：契约是 200 + accepted 计数，采集端据此发现数据被丢。
        var json = $$"""{"segments":[{{SegmentJson(identityKey: "")}}]}""";

        var response = await _handler.HandleAsync("POST", "/v1/segments", Body(json));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("""{"accepted":0}""", response.Body);
    }

    // ---- 采集器发现 + 配置下行（ADR-026 §1/§2） ----

    [Fact]
    public async Task GetConfig_FirstSeen_AutoRegistersEnabled()
    {
        Assert.False(_config.Current.Collectors.ContainsKey("browser"));

        var response = await _handler.HandleAsync("GET", "/v1/collectors/browser/config", Body(""));

        Assert.Equal(200, response.StatusCode);
        Assert.True(response.IsJson);
        Assert.Equal("""{"enabled":true}""", response.Body);
        // "已安装" = 落进注册表，且持久化。
        Assert.True(_config.Current.Collectors.ContainsKey("browser"));
    }

    [Fact]
    public async Task GetConfig_ReportsFlushPeriod_Persisted()
    {
        await _handler.HandleAsync("GET", "/v1/collectors/browser/config", Body(""), "flushPeriodMs=30000");

        Assert.Equal(30000, _config.Current.Collectors["browser"].FlushPeriodMs);
    }

    [Fact]
    public async Task GetConfig_ReflectsUserDisable()
    {
        // 先发现，再由用户（WPF）停用，下次拉取应见 enabled:false（礼貌层据此自停）。
        await _handler.HandleAsync("GET", "/v1/collectors/browser/config", Body(""));
        _config.Update(c => c.Collectors["browser"].Enabled = false);

        var response = await _handler.HandleAsync("GET", "/v1/collectors/browser/config", Body(""));

        Assert.Equal("""{"enabled":false}""", response.Body);
    }

    [Theory]
    [InlineData("/v1/collectors//config")]
    [InlineData("/v1/collectors/a/b/config")]
    [InlineData("/v1/collectors/browser")]
    public async Task GetConfig_MalformedPath_404(string path)
    {
        var response = await _handler.HandleAsync("GET", path, Body(""));

        Assert.Equal(404, response.StatusCode);
    }

    // ---- 强制层停用：disabled source → 403（ADR-026 §4） ----

    [Fact]
    public async Task DisabledSource_403_NothingBuffered()
    {
        _config.Update(c => c.Collectors["browser"] = new Heartbeat.Agent.Models.CollectorEntry { Enabled = false });
        var json = $$"""{"segments":[{{SegmentJson()}}]}""";

        var response = await _handler.HandleAsync("POST", "/v1/segments", Body(json));

        Assert.Equal(403, response.StatusCode);
        Assert.Contains("deactivated", response.Body);
        Assert.Empty(_ingest.GetAndClearSegments());
    }

    [Fact]
    public async Task EnabledSource_200_NotBlocked()
    {
        // 已发现但保持 enabled（默认）不应被 403。
        await _handler.HandleAsync("GET", "/v1/collectors/browser/config", Body(""));
        var json = $$"""{"segments":[{{SegmentJson()}}]}""";

        var response = await _handler.HandleAsync("POST", "/v1/segments", Body(json));

        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task MixedBatch_OneDisabled_403()
    {
        // 批中任一 source 停用即整批拒——采集器不该借搭车绕过停用。
        _config.Update(c => c.Collectors["vscode"] = new Heartbeat.Agent.Models.CollectorEntry { Enabled = false });
        var json = $$"""{"segments":[{{SegmentJson(source: "browser")}},{{SegmentJson(source: "vscode")}}]}""";

        var response = await _handler.HandleAsync("POST", "/v1/segments", Body(json));

        Assert.Equal(403, response.StatusCode);
        Assert.Empty(_ingest.GetAndClearSegments());
    }

    [Fact]
    public async Task PostFromUnknownSource_AutoRegisters()
    {
        // "已安装 = 被 hub 见过"：POST 也算触达，覆盖未实现 config 拉取的采集器。
        var json = $$"""{"segments":[{{SegmentJson()}}]}""";

        await _handler.HandleAsync("POST", "/v1/segments", Body(json));

        Assert.True(_config.Current.Collectors.TryGetValue("browser", out var entry));
        Assert.True(entry!.Enabled);
    }
}
