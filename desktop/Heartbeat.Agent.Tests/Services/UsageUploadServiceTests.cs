using System.Net;
using System.Net.Http.Json;
using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Http;
using Heartbeat.Agent.Services;
using Heartbeat.Agent.Storage;
using Heartbeat.Core.DTOs.Usage;

namespace Heartbeat.Agent.Tests.Services;

public class UsageUploadServiceTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) File.Delete(f);
    }

    private sealed class FakeCache : IUsageCache
    {
        public List<AppUsageItem> Items { get; private set; } = [];
        public int ClearCount { get; private set; }
        public void Add(List<AppUsageItem> items) => Items.AddRange(items);
        public List<AppUsageItem> Load() => new(Items);
        public void Clear() { Items = []; ClearCount++; }
    }

    /// <summary>捕获最后一次发出的 UsageUploadRequest，并返回指定状态码。</summary>
    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public UsageUploadRequest? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = await request.Content!.ReadFromJsonAsync<UsageUploadRequest>(cancellationToken);
            return new HttpResponseMessage(status);
        }
    }

    private (UsageUploadService svc, FakeCache cache, CapturingHandler handler) Build(HttpStatusCode status)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"heartbeat-cfg-{Guid.NewGuid()}.json");
        _tempFiles.Add(tempPath);
        var cm = new ConfigManager(tempPath);
        cm.Update(c => c.ApiBaseUrl = "http://localhost");

        var handler = new CapturingHandler(status);
        var client = new HeartbeatApiClient(new HttpClient(handler), cm);
        var cache = new FakeCache();
        return new(new UsageUploadService(client, cache), cache, handler);
    }

    private static readonly DateTimeOffset Base = new(2025, 6, 1, 10, 0, 0, TimeSpan.Zero);
    private static AppUsageItem Item(string app, int startSec, int endSec) => new()
    {
        AppName = app,
        StartTime = Base.AddSeconds(startSec),
        EndTime = Base.AddSeconds(endSec)
    };

    [Fact]
    public async Task UploadAsync_MergesAdjacentSegments_BeforeSending()
    {
        var (svc, _, handler) = Build(HttpStatusCode.OK);

        // 两段相邻同应用碎片（gap=1s，在容差内）应被合并为一条再发出
        await svc.UploadAsync([Item("VSCode", 0, 60), Item("VSCode", 61, 120)]);

        var sent = handler.LastRequest!.Usages;
        Assert.Single(sent);
        Assert.Equal(Base, sent[0].StartTime);
        Assert.Equal(Base.AddSeconds(120), sent[0].EndTime);
    }

    [Fact]
    public async Task UploadAsync_Failure_CachesMergedSegments()
    {
        var (svc, cache, _) = Build(HttpStatusCode.InternalServerError);

        await svc.UploadAsync([Item("VSCode", 0, 60), Item("VSCode", 61, 120)]);

        // 落盘的也应是已合并的结果
        Assert.Single(cache.Items);
        Assert.Equal(Base.AddSeconds(120), cache.Items[0].EndTime);
    }

    [Fact]
    public async Task UploadAsync_EmptyList_NoOp()
    {
        var (svc, cache, handler) = Build(HttpStatusCode.InternalServerError);

        await svc.UploadAsync([]);

        Assert.Null(handler.LastRequest);
        Assert.Empty(cache.Items);
    }

    [Fact]
    public async Task UploadCachedAsync_MergesCrossBatchFragments_BeforeSending()
    {
        var (svc, cache, handler) = Build(HttpStatusCode.OK);
        // 模拟纯追加缓存里积累的跨批次碎片
        cache.Add([Item("VSCode", 0, 60)]);
        cache.Add([Item("VSCode", 61, 120)]);

        await svc.UploadCachedAsync();

        var sent = handler.LastRequest!.Usages;
        Assert.Single(sent);
        Assert.Equal(Base.AddSeconds(120), sent[0].EndTime);
        Assert.Equal(1, cache.ClearCount);
    }

    [Fact]
    public async Task UploadCachedAsync_Failure_KeepsCache()
    {
        var (svc, cache, _) = Build(HttpStatusCode.InternalServerError);
        cache.Add([Item("VSCode", 0, 60)]);

        await svc.UploadCachedAsync();

        Assert.Single(cache.Items);
        Assert.Equal(0, cache.ClearCount);
    }
}
