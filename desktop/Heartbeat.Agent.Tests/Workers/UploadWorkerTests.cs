using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Http;
using Heartbeat.Agent.Services;
using Heartbeat.Agent.Storage;
using Heartbeat.Agent.Workers;
using Heartbeat.Core;
using Heartbeat.Core.DTOs.Input;
using Heartbeat.Core.DTOs.Segments;
using System.Net;

namespace Heartbeat.Agent.Tests.Workers;

/// <summary>
/// 出网调度契约（ADR-022）：一轮 = 两条流各 drain 一次 + 段批次触发图标挂点；
/// StopAsync 执行终态 drain（与注册顺序共同构成"关机不丢尾巴"，见 AgentHostExtensionsTests）。
/// </summary>
public class UploadWorkerTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            if (File.Exists(f)) File.Delete(f);
    }

    private sealed class FakeSource<T> : IUploadSource<T>
    {
        public List<T> Items { get; } = [];
        public List<T> Reinjected { get; } = [];

        public List<T> Drain()
        {
            var copy = new List<T>(Items);
            Items.Clear();
            return copy;
        }

        public void Reinject(List<T> items) => Reinjected.AddRange(items);
    }

    private sealed class FakeCache<T> : ICache<T>
    {
        private List<T> _items = [];
        public void Add(List<T> items) => _items.AddRange(items);
        public List<T> Load() => new(_items);
        public void Clear() => _items = [];
    }

    private sealed class FakeIconUploader : IIconUploadService
    {
        public List<string> Calls { get; } = [];
        public string? ThrowFor { get; set; }

        public Task EnsureIconUploadedAsync(string appName)
        {
            Calls.Add(appName);
            if (string.Equals(appName, ThrowFor, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("icon boom");
            return Task.CompletedTask;
        }
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private (UploadWorker worker, FakeSource<ActivitySegmentItem> segSource, FakeSource<InputEventItem> inputSource, FakeIconUploader icons) Build()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"heartbeat-cfg-{Guid.NewGuid()}.json");
        _tempFiles.Add(tempPath);
        var cm = new ConfigManager(tempPath);

        var api = new HeartbeatApiClient(new HttpClient(new OkHandler()));
        var segSource = new FakeSource<ActivitySegmentItem>();
        var inputSource = new FakeSource<InputEventItem>();
        var icons = new FakeIconUploader();

        var segStream = new UploadStream<ActivitySegmentItem>(
            "段", segSource,
            batch => api.UploadSegmentsAsync(new SegmentUploadRequest { Segments = batch }),
            new FakeCache<ActivitySegmentItem>(),
            SnapshotCompaction.KeepLatest);
        var inputStream = new UploadStream<InputEventItem>(
            "输入事件", inputSource,
            batch => api.UploadInputEventsAsync(new InputEventUploadRequest { Events = batch }),
            new FakeCache<InputEventItem>());

        return (new UploadWorker(icons, segStream, inputStream, cm, new DeclarationUplinkService(api, cm)), segSource, inputSource, icons);
    }

    private static ActivitySegmentItem Segment(string? appName)
    {
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        return new ActivitySegmentItem
        {
            Id = Guid.CreateVersion7(),
            Source = "system",
            IdentityKey = $"{appName}|t",
            AppName = appName,
            StartTime = t0,
            EndTime = t0.AddSeconds(60)
        };
    }

    private static InputEventItem Event() => new()
    {
        Id = Guid.CreateVersion7(),
        EventType = InputEventType.KeyDown,
        Code = 65,
        Timestamp = DateTimeOffset.UtcNow.AddMinutes(-1)
    };

    [Fact]
    public async Task DrainOnce_DrainsBothStreams()
    {
        var (worker, segSource, inputSource, _) = Build();
        segSource.Items.Add(Segment("Code"));
        inputSource.Items.AddRange([Event(), Event()]);

        await worker.DrainOnceAsync();

        Assert.Empty(segSource.Items);
        Assert.Empty(inputSource.Items);
    }

    [Fact]
    public async Task DrainOnce_TriggersIcons_DistinctCaseInsensitive_SkipsEmpty()
    {
        var (worker, segSource, _, icons) = Build();
        segSource.Items.AddRange([Segment("Code"), Segment("code"), Segment("mpv"), Segment(null)]);

        await worker.DrainOnceAsync();

        Assert.Equal(2, icons.Calls.Count);
        Assert.Contains("Code", icons.Calls);
        Assert.Contains("mpv", icons.Calls);
    }

    [Fact]
    public async Task DrainOnce_IconFailure_DoesNotAbortRemainingApps()
    {
        var (worker, segSource, _, icons) = Build();
        icons.ThrowFor = "Code";
        segSource.Items.AddRange([Segment("Code"), Segment("mpv")]);

        await worker.DrainOnceAsync(); // 不抛出

        Assert.Equal(2, icons.Calls.Count); // 单个应用失败不中断其余
    }

    [Fact]
    public async Task StopAsync_PerformsFinalDrain()
    {
        // 关机不丢尾巴的 worker 侧：monitor 停止时推入 hub 的终态快照由本次 drain 带走（ADR-020 §6）
        var (worker, segSource, inputSource, _) = Build();
        segSource.Items.Add(Segment("Code"));
        inputSource.Items.Add(Event());

        await worker.StopAsync(CancellationToken.None);

        Assert.Empty(segSource.Items);
        Assert.Empty(inputSource.Items);
    }
}
