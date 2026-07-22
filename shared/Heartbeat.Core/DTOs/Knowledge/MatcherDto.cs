namespace Heartbeat.Core.DTOs.Knowledge
{
    /// <summary>Matcher 谓词词汇表（ADR-029 §3）：刻意收窄成三种起步，防止长成规则引擎。</summary>
    public static class MatcherOps
    {
        public const string Equal = "equals";
        public const string Prefix = "prefix";
        public const string Contains = "contains";
    }

    /// <summary>
    /// 路径谓词的一步：某观测读数上的一个谓词。**不带层号**（ADR-030 §6）：读数名在
    /// source 内唯一（采集器声明校验），深度是声明的展示 / 隐私属性，不进 Matcher——
    /// 采集器重排 / 提拔深度层永不失效存量指纹。
    /// </summary>
    public class MatcherStepDto
    {
        /// <summary>读数名（采集器声明词汇：system 的 app/title、browser 的 url/tab_title…）。</summary>
        public string Reading { get; set; } = string.Empty;

        /// <summary>谓词：equals / prefix / contains（见 MatcherOps）。</summary>
        public string Op { get; set; } = string.Empty;

        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// Matcher（匹配子，ADR-029 §3）：知识层指纹原子——沿某 Source 深度树的路径谓词，
    /// 各步合取，单步是退化形态。digest 是深度树的观测投影，Matcher 是同一棵树上的路径谓词。
    /// </summary>
    public class MatcherDto
    {
        public string Source { get; set; } = string.Empty;

        public List<MatcherStepDto> Steps { get; set; } = [];
    }
}
