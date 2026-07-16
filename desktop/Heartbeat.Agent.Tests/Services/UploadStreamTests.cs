using Heartbeat.Agent.Http;
using Heartbeat.Agent.Services;
using Heartbeat.Agent.Storage;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Segments;
using System.Net;

namespace Heartbeat.Agent.Tests.Services;

/// <summary>
/// 上传流契约（ADR-020/022）：drain 一轮 = 先重传缓存，再取 fresh 出网——
/// 送达，或落离线缓存，否则重注入源。"批次不蒸发"由流自持。
/// 经真实 HeartbeatApiClient + 桩 HttpMessageHandler 驱动，传输层（URL/负载）一并覆盖。
/// </summary>
public class UploadStreamTests
{
    private sealed class FakeSource : IUploadSource<ActivitySegmentItem>
    {
        public List<ActivitySegmentItem> Items { get; } = [];
        public List<ActivitySegmentItem> Reinjected { get; } = [];

        public List<ActivitySegmentItem> Drain()
        {
            var copy = new List<ActivitySegmentItem>(Items);
            Items.Clear();
            return copy;
        }

        public void Reinject(List<ActivitySegmentItem> items) => Reinjected.AddRange(items);
    }

    private sealed class FakeCache : ICache<ActivitySegmentItem>
    {
        public List<ActivitySegmentItem> Items { get; private set; } = [];
        public int ClearCount { get; private set; }
        public bool ThrowOnAdd { get; set; }

        public void Add(List<ActivitySegmentItem> items)
        {
            if (ThrowOnAdd) throw new IOException("disk full");
            Items.AddRange(items);
        }

        public List<ActivitySegmentItem> Load() => new(Items);
        public void Clear() { Items = []; ClearCount++; }
    }

    private sealed class CapturingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), body));
            return new HttpResponseMessage(status);
        }
    }

    private static (UploadStream<ActivitySegmentItem> stream, FakeSource source, FakeCache cache, CapturingHandler handler) Build(HttpStatusCode status)
    {
        var handler = new CapturingHandler(status);
        var api = new HeartbeatApiClient(new HttpClient(handler));
        var source = new FakeSource();
        var cache = new FakeCache();

        // 与 AgentHostExtensions 的段流同构：compact 策略 = KeepLatest，只作用于出缓存的批
        var stream = new UploadStream<ActivitySegmentItem>(
            "段",
            source,
            batch => api.UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }),
            cache,
            SnapshotCompaction.KeepLatest);
        return (stream, source, cache, handler);
    }

    private static ActivitySegmentItem Segment(Guid? id = null, int endSec = 60)
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        return new ActivitySegmentItem
        {
            Id = id ?? Guid.CreateVersion7(),
            Source = "browser",
            IdentityKey = "https://example.com",
            StartTime = t0,
            EndTime = t0.AddSeconds(endSec)
        };
    }

    private static SegmentUploadRequest ParseBody(string body) =>
        System.Text.Json.JsonSerializer.Deserialize<SegmentUploadRequest>(
            body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    [Fact]
    public async Task Drain_Success_SendsFresh_EmptiesSource_DoesNotCache()
    {
        var (stream, source, cache, handler) = Build(HttpStatusCode.OK);
        source.Items.AddRange([Segment(), Segment()]);

        var drained = await stream.DrainAsync();

        Assert.Equal(2, drained.Count);       // 返回本轮取走的 fresh 批（图标挂点用）
        Assert.Empty(source.Items);
        Assert.Empty(source.Reinjected);
        Assert.Empty(cache.Items);
        var req = Assert.Single(handler.Requests);
        Assert.EndsWith("/api/v1/segments", req.Url);
    }

    [Fact]
    public async Task Drain_SendFails_CachesItems_NoReinject()
    {
        var (stream, source, cache, _) = Build(HttpStatusCode.InternalServerError);
        source.Items.AddRange([Segment(), Segment()]);

        var drained = await stream.DrainAsync();

        Assert.Equal(2, drained.Count);
        Assert.Equal(2, cache.Items.Count);
        Assert.Empty(source.Reinjected);
    }

    [Fact]
    public async Task Drain_DoubleFailure_ReinjectsToSource()
    {
        // 不蒸发不变量（ADR-022）：既没送达也没缓存住 → 流自己重注入源
        var (stream, source, cache, _) = Build(HttpStatusCode.InternalServerError);
        cache.ThrowOnAdd = true;
        var a = Segment();
        var b = Segment();
        source.Items.AddRange([a, b]);

        var drained = await stream.DrainAsync();

        Assert.Equal(2, drained.Count);
        Assert.Equal(2, source.Reinjected.Count);
        Assert.Same(a, source.Reinjected[0]); // 原样退回，不复制不丢字段
        Assert.Same(b, source.Reinjected[1]);
    }

    [Fact]
    public async Task Drain_EmptySourceAndCache_NoRequest()
    {
        var (stream, _, _, handler) = Build(HttpStatusCode.OK);

        var drained = await stream.DrainAsync();

        Assert.Empty(drained);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Drain_RetriesCachedFirst_ThenFresh_ClearsCache()
    {
        // cached 先于 fresh（流内局部时序，ADR-022）：离线恢复后先清积压
        var (stream, source, cache, handler) = Build(HttpStatusCode.OK);
        var cachedSeg = Segment();
        var freshSeg = Segment();
        cache.Add([cachedSeg]);
        source.Items.Add(freshSeg);

        await stream.DrainAsync();

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(cachedSeg.Id, Assert.Single(ParseBody(handler.Requests[0].Body).Segments).Id);
        Assert.Equal(freshSeg.Id, Assert.Single(ParseBody(handler.Requests[1].Body).Segments).Id);
        Assert.Empty(cache.Items);
        Assert.Equal(1, cache.ClearCount);
    }

    [Fact]
    public async Task Drain_CachedSendFails_KeepsCache_StillDrainsFresh()
    {
        var (stream, source, cache, handler) = Build(HttpStatusCode.InternalServerError);
        cache.Add([Segment(), Segment()]);
        source.Items.Add(Segment());

        await stream.DrainAsync();

        // 缓存重传失败：保留不清（ADR-008）；fresh 照常尝试，失败后追加入缓存
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(0, cache.ClearCount);
        Assert.Equal(3, cache.Items.Count);
        Assert.Empty(source.Reinjected);
    }

    [Fact]
    public async Task Drain_CompactsCachedBatchOnly_NotFresh()
    {
        // 缓存纯追加，离线期间积累同 Id 快照 → 出网前 KeepLatest（ADR-018）；
        // fresh 批来自按 Id 键控的 buffer，天然无重复，不压缩
        var (stream, source, cache, handler) = Build(HttpStatusCode.OK);
        var id = Guid.CreateVersion7();
        cache.Add([Segment(id, endSec: 30), Segment(id, endSec: 60), Segment(id, endSec: 90)]);
        source.Items.AddRange([Segment(), Segment()]);

        await stream.DrainAsync();

        Assert.Equal(2, handler.Requests.Count);
        var cachedSent = Assert.Single(ParseBody(handler.Requests[0].Body).Segments);
        Assert.Equal(id, cachedSent.Id);
        Assert.Equal(2, ParseBody(handler.Requests[1].Body).Segments.Count);
    }
}
