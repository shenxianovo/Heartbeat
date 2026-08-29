namespace Heartbeat.Core.DTOs.Knowledge
{
    /// <summary>
    /// 两阶段教学第二步的请求（ADR-031 §6）：用户对证据卡的自然语言回答。
    /// 证据本体不由客户端提供——服务端按 (Owner, WindowKey, 问题 Id) 取回自己发出的证据，
    /// 第二阶段只能解释用户实际看过的东西，不接受任意 Owner/Segment ID。
    /// </summary>
    public class ProposeFromQuestionRequest
    {
        /// <summary>问题响应携带的 Analytics submission credential；必须匹配当前请求窗口。</summary>
        public string WindowKey { get; set; } = string.Empty;

        /// <summary>用户的自然语言解释。</summary>
        public string Answer { get; set; } = string.Empty;
    }

    /// <summary>
    /// Recap 纠正入口的请求（ADR-031 §6，issue 06）：用户从某日 Recap 发起的自然语言纠正。
    /// 证据上下文由服务端锁定为该本地日期的 Observation/Segment 窗口——散文只是用户正在
    /// 纠正的显示上下文，事实证据来自目标日观察与用户的话，不从散文自动抽取知识。
    /// </summary>
    public class ProposeCorrectionRequest
    {
        /// <summary>纠正的目标日期（带调用方时区 offset，与 recap 读取同约）。</summary>
        public DateTimeOffset Date { get; set; }

        /// <summary>用户的自然语言纠正：遗漏、错误关联或应记住的私人语境。</summary>
        public string Correction { get; set; } = string.Empty;
    }

    /// <summary>
    /// LLM 整理出的知识变更提案（ADR-031 §6）：只是提案，没有任何写入发生。
    /// 四个字段明确区分：模型解释 / 可编辑的结构化操作 / 约束警告 / 无需保存的建议。
    /// 用户可逐项编辑、取消，确认后把选中的操作提交到 commit 端点。
    /// </summary>
    public class KnowledgeProposalResponse
    {
        /// <summary>模型对用户回答的理解复述（人话，供用户核对）。</summary>
        public string Explanation { get; set; } = string.Empty;

        /// <summary>
        /// 结构化操作：已有对象一律按 UUIDv7 引用并已由服务端盖上读取时版本；
        /// 同提案内新建对象按 OpId 临时引用。
        /// </summary>
        public List<KnowledgeOperationDto> Operations { get; set; } = [];

        /// <summary>约束警告：被剔除的越权/虚构引用、无法表达的意图等。</summary>
        public List<string> Warnings { get; set; } = [];

        /// <summary>无需保存的建议（如"这看起来是一次性的，不必建脉络"）。</summary>
        public List<string> Suggestions { get; set; } = [];

        /// <summary>读数展示名词典（ADR-030 §7），供操作里的 Matcher 渲染。</summary>
        public Dictionary<string, string> ReadingLabels { get; set; } = [];
    }
}
