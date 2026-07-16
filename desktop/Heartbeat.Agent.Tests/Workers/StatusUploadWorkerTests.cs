using Heartbeat.Agent.Http;
using Heartbeat.Agent.Services;
using Heartbeat.Agent.Workers;
using System.Net;
using System.Text.Json;

namespace Heartbeat.Agent.Tests.Workers;

/// <summary>
/// presence 心跳契约（ADR-021）：启动即推一次；Current Activity 变更立刻补推（事件＝新鲜度）；
/// 周期 keepalive（30s 常量）由信号量超时驱动，不在此测（时序型，行为等价于今天的周期循环）。
/// </summary>
public class StatusUploadWorkerTests
{
    private sealed class FakeStatus : ICollectionStatus
    {
        private string? _current;
        public string? CurrentApp => _current;
        public event Action<string?>? CurrentAppChanged;
        public IReadOnlyDictionary<string, DateTimeOffset> SourceLastSeen => new Dictionary<string, DateTimeOffset>();

        public void Set(string? app)
        {
            _current = app;
            CurrentAppChanged?.Invoke(app);
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly object _lock = new();
        private readonly List<string> _bodies = [];

        public int Count { get { lock (_lock) return _bodies.Count; } }
        public string Body(int index) { lock (_lock) return _bodies[index]; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_lock) _bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static (StatusUploadWorker worker, FakeStatus status, CapturingHandler handler) Build()
    {
        var handler = new CapturingHandler();
        var api = new HeartbeatApiClient(new HttpClient(handler));
        var status = new FakeStatus();
        return (new StatusUploadWorker(status, api), status, handler);
    }

    private static string? CurrentAppOf(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("currentApp").GetString();
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
        status.Set("vscode");

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => handler.Count >= 1);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("vscode", CurrentAppOf(handler.Body(0)));
    }

    [Fact]
    public async Task Change_TriggersImmediateHeartbeat_AwayAsIs()
    {
        // 变了就推（ADR-021）：keepalive 常量 30s，5s 内到达的第二个心跳只能来自变更信号
        var (worker, status, handler) = Build();
        status.Set("vscode");

        await worker.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => handler.Count >= 1);

        status.Set("__away__");
        await WaitUntilAsync(() => handler.Count >= 2);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal("__away__", CurrentAppOf(handler.Body(1)));
    }
}
