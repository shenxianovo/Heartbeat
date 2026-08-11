using Heartbeat.Hub.Core.Http;
using Heartbeat.Hub.Core.Presence;
using Heartbeat.Hub.Core.Runtime;
using System.Net;
using System.Text.Json;

namespace Heartbeat.Hub.Core.Tests.Runtime;

/// <summary>
/// presence 心跳契约（ADR-021）：启动即推一次；Current Activity 变更立刻补推（事件＝新鲜度）；
/// 周期 keepalive（30s 常量）由信号量超时驱动，不在此测（时序型，行为等价于今天的周期循环）。
/// </summary>
public class StatusUploadWorkerTests
{
    private sealed class FakeStatus : ICollectionStatus
    {
        public CurrentActivity? CurrentActivity { get; private set; }
        public event Action<CurrentActivity?>? CurrentActivityChanged;
        public IReadOnlyDictionary<string, DateTimeOffset> SourceLastSeen => new Dictionary<string, DateTimeOffset>();

        public void Set(string? appIdentityKey, string? displayName = null)
        {
            CurrentActivity = appIdentityKey == null ? null : new CurrentActivity(appIdentityKey, displayName);
            CurrentActivityChanged?.Invoke(CurrentActivity);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly object _lock = new();
        private readonly List<string> _bodies = [];

        public int Count { get { lock (_lock) return _bodies.Count; } }
        public string Body(int index) { lock (_lock) return _bodies[index]; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_lock) _bodies.Add(body);
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Status == HttpStatusCode.UpgradeRequired ? "update required" : "")
            };
        }
    }

    private static (StatusUploadWorker worker, FakeStatus status, CapturingHandler handler) Build()
    {
        var handler = new CapturingHandler();
        var api = new HeartbeatApiClient(new HttpClient(handler));
        var status = new FakeStatus();
        return (new StatusUploadWorker(status, api), status, handler);
    }

    private static (string? IdentityKey, string? DisplayName, string? LegacyApp) CurrentActivityOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return (
            doc.RootElement.GetProperty("currentAppIdentityKey").GetString(),
            doc.RootElement.GetProperty("currentAppDisplayName").GetString(),
            doc.RootElement.TryGetProperty("currentApp", out var legacy) ? legacy.GetString() : null);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
            await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task Start_SendsInitialHeartbeat_WithCurrentApp()
    {
        var (worker, status, handler) = Build();
        status.Set("win:code", "Code");

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => handler.Count >= 1);
        await worker.StopAsync(CancellationToken.None);

        var current = CurrentActivityOf(handler.Body(0));
        Assert.Equal("win:code", current.IdentityKey);
        Assert.Equal("Code", current.DisplayName);
        Assert.Null(current.LegacyApp);
    }

    [Fact]
    public async Task Change_TriggersImmediateHeartbeat_AwayAsIs()
    {
        // 变了就推（ADR-021）：keepalive 常量 30s，5s 内到达的第二个心跳只能来自变更信号
        var (worker, status, handler) = Build();
        status.Set("win:code", "Code");

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => handler.Count >= 1);

        status.Set("sys:away", "离开");
        await WaitUntilAsync(() => handler.Count >= 2);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("sys:away", CurrentActivityOf(handler.Body(1)).IdentityKey);
    }

    [Fact]
    public async Task UpgradeRequired_IsExposedAndSuppressesRepeatedPresenceRequests()
    {
        var handler = new CapturingHandler { Status = HttpStatusCode.UpgradeRequired };
        var api = new HeartbeatApiClient(new HttpClient(handler));
        var status = new FakeStatus();
        var compatibility = new ClientCompatibilityStatus();
        var worker = new StatusUploadWorker(status, api, compatibility);
        status.Set("win:code", "Code");

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => handler.Count >= 1);
        status.Set("win:mpv", "mpv");
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(compatibility.Current.UpdateRequired);
        Assert.Contains("update required", compatibility.Current.Message);
        Assert.Equal(1, handler.Count);
    }
}
