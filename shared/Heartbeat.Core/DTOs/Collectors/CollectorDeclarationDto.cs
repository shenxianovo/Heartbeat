namespace Heartbeat.Core.DTOs.Collectors
{
    /// <summary>
    /// 采集器观测深度表声明（ADR-030 §1）：采集器运行时上报的纯数据契约。
    /// 读数命名与人话标签归采集器主权；from 只指运输槽位（ADR-030 §2），不携带语义。
    /// </summary>
    public class CollectorDeclarationDto
    {
        public string Source { get; set; } = string.Empty;

        /// <summary>契约版本（深度表变更才递增）：生效规则取每 source 的 max(Version)。</summary>
        public int Version { get; set; }

        /// <summary>采集器软件版本（如扩展的 1.4.2），纯诊断元数据。</summary>
        public string? CollectorVersion { get; set; }

        /// <summary>观测深度层，浅 → 深，有序。</summary>
        public List<DepthLayerDto> Layers { get; set; } = [];
    }

    /// <summary>一层观测深度。层内读数数组留门（解释器约定首读数为分解轴），当前全部单读数。</summary>
    public class DepthLayerDto
    {
        public List<DepthReadingDto> Readings { get; set; } = [];
    }

    /// <summary>一个观测读数的声明。</summary>
    public class DepthReadingDto
    {
        /// <summary>读数名（采集器词汇，source 内唯一，小写 canonical——与 Matcher 身份同尺）。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>运输槽位：appName | title | identityKey | attributes.&lt;path&gt;。</summary>
        public string From { get; set; } = string.Empty;

        /// <summary>人话展示名（前端标签），可空回落读数名。</summary>
        public string? Label { get; set; }
    }
}
