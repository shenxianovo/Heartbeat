namespace Heartbeat.Agent.Models
{
    public class AgentConfig
    {
        // ApiBaseUrl / AuthServiceBaseUrl 已退役：服务端点为代码常量（Configuration.Endpoints），
        // 旧 config.json 中的字段被反序列化静默忽略。
        public string ApiKey { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;

        private int _uploadIntervalMinutes = 1;
        public int UploadIntervalMinutes
        {
            get => _uploadIntervalMinutes;
            set => _uploadIntervalMinutes = value < 1 ? 1 : value;
        }

        // StatusUploadIntervalSeconds 已随 ADR-021 退役：心跳节律为代码常量
        // （StatusUploadWorker.KeepaliveInterval），旧 config.json 中的字段被反序列化静默忽略。

        /// <summary>
        /// 前台进程名命中此列表时，该使用段被归一化为 away 段（仅改名，不驱动 away 状态机）。
        /// 默认包含锁屏宿主 LockApp。详见 ADR-014。
        /// </summary>
        public List<string> AwayProcessNames { get; set; } = ["LockApp"];

        /// <summary>
        /// 本地 ingest 枢纽监听基准端口（loopback），插件采集器往此推段（ADR-017）。
        /// 被占时向上顺延试绑（范围见 SegmentIngestWorker.PortRange），采集器按同一范围探测发现。
        /// 默认 24820 位于 Windows（49152+）与 Linux（32768+，WSL mirrored 模式共享端口空间）
        /// 动态端口范围之外——曾因默认值落在 WSL 动态频段内被幽灵预留成片封锁。
        /// ≤0 表示禁用。
        /// </summary>
        public int IngestPort { get; set; } = 24820;

        /// <summary>
        /// 采集器注册表（ADR-026）：source → 条目。hub 首次见到某采集器（其 GET config）时自动记入，
        /// 即"已安装"的定义；关闭浏览器或 Agent 重启不丢。用户翻 enabled 也写此处。
        /// </summary>
        public Dictionary<string, CollectorEntry> Collectors { get; set; } = [];
    }
}
