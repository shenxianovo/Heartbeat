using Heartbeat.Agent.Configuration;
using Heartbeat.Agent.Services;
using Microsoft.Extensions.Hosting;
using Serilog;
using System.Net;

namespace Heartbeat.Agent.Workers
{
    /// <summary>
    /// 本地 ingest 枢纽（ADR-017）：在 loopback 上开 HTTP 接口，接收插件采集器
    /// （浏览器扩展 / VSCode 插件 / 游戏模组）推送的已折叠段，进入统一上传管线。
    /// 仅绑定 127.0.0.1——信任模型为"本机进程可信"（单用户自部署，ADR-017 §1）。
    /// 协议逻辑（路由/解析/守卫/状态码）在 SegmentIngestRequestHandler，
    /// 本类只负责 HttpListener 生命周期与上下文搬运（ADR-020）。
    /// </summary>
    public class SegmentIngestWorker(
        SegmentIngestRequestHandler handler,
        ConfigManager configManager) : BackgroundService
    {
        /// <summary>
        /// 端口浮动范围：基准端口被占时向上顺延试绑的端口数。
        /// 采集器按同一范围探测（GET /v1/hub 验身份），两侧约定一致。
        /// </summary>
        public const int PortRange = 10;

        /// <summary>范围内全部被占时的重试间隔：占用者（如未退净的旧实例）通常短时间内让位。</summary>
        private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var basePort = configManager.Current.IngestPort;
            if (basePort <= 0)
            {
                Log.Information("本地 ingest 枢纽未启用（ingestPort = {Port}）", basePort);
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                using var listener = TryStartListener(basePort, out var port);
                if (listener == null)
                {
                    Log.Warning(
                        "本地 ingest 枢纽启动失败（端口 {BasePort}–{EndPort} 均被占用），{Retry} 秒后重试",
                        basePort, basePort + PortRange - 1, RetryInterval.TotalSeconds);
                    try
                    {
                        await Task.Delay(RetryInterval, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    continue;
                }

                Log.Information("本地 ingest 枢纽已启动: http://127.0.0.1:{Port}/", port);
                await ServeAsync(listener, stoppingToken);
                Log.Information("本地 ingest 枢纽已停止");
                return;
            }
        }

        /// <summary>从基准端口起顺延试绑 <see cref="PortRange"/> 个端口，全占返回 null。</summary>
        private static HttpListener? TryStartListener(int basePort, out int boundPort)
        {
            for (var port = basePort; port < basePort + PortRange && port <= 65535; port++)
            {
                var listener = new HttpListener();
                // loopback 限定：非本机流量到不了这个前缀。
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    listener.Start();
                    boundPort = port;
                    return listener;
                }
                catch (HttpListenerException ex)
                {
                    listener.Close();
                    Log.Debug(ex, "端口 {Port} 被占用，尝试下一个", port);
                }
            }
            boundPort = 0;
            return null;
        }

        private async Task ServeAsync(HttpListener listener, CancellationToken stoppingToken)
        {
            using var _ = stoppingToken.Register(listener.Stop);

            while (!stoppingToken.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync();
                }
                catch (Exception) when (stoppingToken.IsCancellationRequested)
                {
                    break; // Stop() 会让 GetContextAsync 抛出
                }

                // 逐请求串行处理即可：本机插件低频小批量，无并发压力。
                try
                {
                    await HandleRequestAsync(ctx);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "ingest 请求处理异常");
                    TryRespond(ctx, 500, "internal error");
                }
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            // Url.Query 含前导 '?'；剥掉传给协议层（GET config 的 flushPeriodMs 由此读）。
            var query = req.Url?.Query is { Length: > 0 } q ? q.TrimStart('?') : null;
            var response = await handler.HandleAsync(req.HttpMethod, req.Url?.AbsolutePath, req.InputStream, query);
            TryRespond(ctx, response.StatusCode, response.Body, response.IsJson);
        }

        private static void TryRespond(HttpListenerContext ctx, int status, string body, bool json = false)
        {
            try
            {
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = json ? "application/json" : "text/plain";
                using var writer = new StreamWriter(ctx.Response.OutputStream);
                writer.Write(body);
            }
            catch
            {
                // 客户端可能已断开；忽略。
            }
        }
    }
}
