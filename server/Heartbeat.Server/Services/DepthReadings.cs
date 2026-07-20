using Heartbeat.Core;

namespace Heartbeat.Server.Services
{
    /// <summary>深度树上的一个读数：某观测深度层上的 (读数名, 值)。</summary>
    public readonly record struct DepthReading(int Layer, string Reading, string Value);

    /// <summary>
    /// 观测深度读数提取（ADR-029 §2）：镜像各采集器契约声明的深度表，纯函数。
    /// 读数命名归各采集器契约，此处只是 server 侧镜像：
    /// - system：L1 app（进程/App）、L2 title（窗口标题）
    /// - browser：L1 url（规范化 URL，即 IdentityKey）、L1 tab_title（标签页标题）——同深双读数；
    ///   L2 内容摘要 / L3 DOM 留待 HTML 采集落地时由 browser 契约追加
    /// - 其他源：L1 identity（IdentityKey）、L2 title 兜底——新采集器带自己的深度表来插
    /// </summary>
    public static class DepthReadings
    {
        public static IReadOnlyList<DepthReading> For(string source, string? appName, string? title, string identityKey)
        {
            var readings = new List<DepthReading>(2);
            if (source == ActivitySources.System)
            {
                readings.Add(new(1, "app", string.IsNullOrWhiteSpace(appName) ? "(unknown)" : appName));
                if (!string.IsNullOrWhiteSpace(title)) readings.Add(new(2, "title", title));
            }
            else if (source == ActivitySources.Browser)
            {
                readings.Add(new(1, "url", identityKey));
                if (!string.IsNullOrWhiteSpace(title)) readings.Add(new(1, "tab_title", title));
            }
            else
            {
                readings.Add(new(1, "identity", identityKey));
                if (!string.IsNullOrWhiteSpace(title)) readings.Add(new(2, "title", title));
            }
            return readings;
        }
    }
}
