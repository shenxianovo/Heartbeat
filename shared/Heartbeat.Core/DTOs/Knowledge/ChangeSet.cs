namespace Heartbeat.Core.DTOs.Knowledge
{
    /// <summary>
    /// KnowledgeChangeSet 的操作词汇（ADR-031 §6）：三个教学入口（主动发问 / Recap 纠正 /
    /// 手动管理）共用的知识写模型。字符串小写驼峰，机器可判。
    /// </summary>
    public static class KnowledgeOpTypes
    {
        public const string CreateStrand = "createStrand";
        public const string UpdateStrand = "updateStrand";
        public const string MoveStrand = "moveStrand";
        public const string EndStrand = "endStrand";
        public const string BindMatcher = "bindMatcher";
        public const string MuteMatcher = "muteMatcher";
        public const string CreateEpisode = "createEpisode";
        public const string UpdateEpisode = "updateEpisode";
        public const string RelateEpisode = "relateEpisode";
        public const string CreateProbe = "createProbe";
        public const string ResolveProbe = "resolveProbe";
        public const string PromoteEpisode = "promoteEpisode";

        public static readonly IReadOnlyList<string> All =
        [
            CreateStrand, UpdateStrand, MoveStrand, EndStrand,
            BindMatcher, MuteMatcher,
            CreateEpisode, UpdateEpisode, RelateEpisode,
            CreateProbe, ResolveProbe, PromoteEpisode,
        ];
    }

    /// <summary>
    /// Strand 引用：已有对象按 UUIDv7（StrandId），同一 change set 内新建对象按 OpId 临时引用。
    /// 恰好一个非空；被引用的操作必须排在前面。
    /// </summary>
    public class StrandRefDto
    {
        public Guid? StrandId { get; set; }

        public string? OpId { get; set; }
    }

    /// <summary>Episode 引用：语义同 StrandRefDto。</summary>
    public class EpisodeRefDto
    {
        public Guid? EpisodeId { get; set; }

        public string? OpId { get; set; }
    }

    /// <summary>新建 Strand 操作：父级可为同 set 内新建节点。</summary>
    public class CreateStrandOpDto
    {
        public string Name { get; set; } = string.Empty;

        public string Gloss { get; set; } = string.Empty;

        /// <summary>null = 顶层。</summary>
        public StrandRefDto? Parent { get; set; }

        public DateOnly? StartedOn { get; set; }

        public DateOnly? EndedOn { get; set; }

        public List<MatcherDto> Members { get; set; } = [];
    }

    /// <summary>
    /// 编辑已有 Strand（按 UUIDv7）：名字/释义/日期覆盖。Members null = 保留现有指纹
    /// （LLM 提案不必回显整组成员）；非 null = 整组替换。ExpectedVersion 为提案读取时版本。
    /// </summary>
    public class UpdateStrandOpDto
    {
        public Guid StrandId { get; set; }

        public long ExpectedVersion { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Gloss { get; set; } = string.Empty;

        public DateOnly? StartedOn { get; set; }

        public DateOnly? EndedOn { get; set; }

        /// <summary>null = 保留现有成员；非 null = 整组替换。</summary>
        public List<MatcherDto>? Members { get; set; }
    }

    /// <summary>移动 Strand（纠错语义，ADR-031 §2）。新父级可为同 set 内新建节点；null = 顶层。</summary>
    public class MoveStrandOpDto
    {
        public Guid StrandId { get; set; }

        public long ExpectedVersion { get; set; }

        public StrandRefDto? NewParent { get; set; }
    }

    /// <summary>结束 Strand。</summary>
    public class EndStrandOpDto
    {
        public Guid StrandId { get; set; }

        public long ExpectedVersion { get; set; }

        public DateOnly EndedOn { get; set; }
    }

    /// <summary>为 Strand 追加一个 Matcher（canonical 已存在则收敛）。目标为已有对象时必须携带版本。</summary>
    public class BindMatcherOpDto
    {
        public StrandRefDto Strand { get; set; } = new();

        /// <summary>StrandId 引用时必填；OpId 引用时由提交端按 set 内实际版本补全。</summary>
        public long? ExpectedVersion { get; set; }

        public MatcherDto Matcher { get; set; } = new();
    }

    /// <summary>静音一个 Matcher（幂等）。</summary>
    public class MuteMatcherOpDto
    {
        public MatcherDto Matcher { get; set; } = new();
    }

    /// <summary>新建 Episode：关联 Strand 可为同 set 内新建节点。</summary>
    public class CreateEpisodeOpDto
    {
        public DateOnly LocalDate { get; set; }

        public string Text { get; set; } = string.Empty;

        public DateTimeOffset? ApproximateStart { get; set; }

        public DateTimeOffset? ApproximateEnd { get; set; }

        public StrandRefDto? RelatedStrand { get; set; }
    }

    /// <summary>编辑已有 Episode（按 UUIDv7）。</summary>
    public class UpdateEpisodeOpDto
    {
        public Guid EpisodeId { get; set; }

        public long ExpectedVersion { get; set; }

        public DateOnly LocalDate { get; set; }

        public string Text { get; set; } = string.Empty;

        public DateTimeOffset? ApproximateStart { get; set; }

        public DateTimeOffset? ApproximateEnd { get; set; }
    }

    /// <summary>关联 / 解除 Episode 的最具体 Strand（RelatedStrand null = 解除）。</summary>
    public class RelateEpisodeOpDto
    {
        public EpisodeRefDto Episode { get; set; } = new();

        /// <summary>EpisodeId 引用时必填；OpId 引用时由提交端补全。</summary>
        public long? ExpectedVersion { get; set; }

        public StrandRefDto? RelatedStrand { get; set; }
    }

    /// <summary>在 Episode 上创建 RecurrenceProbe。</summary>
    public class CreateProbeOpDto
    {
        public EpisodeRefDto Episode { get; set; } = new();

        public MatcherDto Matcher { get; set; } = new();
    }

    /// <summary>解决已有 Probe：denied / muted（promoted 只由提升操作写）。</summary>
    public class ResolveProbeOpDto
    {
        public Guid ProbeId { get; set; }

        public string Resolution { get; set; } = string.Empty;
    }

    /// <summary>
    /// 非破坏性提升（ADR-031 §5）：目标 Strand 既可是已有对象也可是同 set 内新建节点——
    /// "新建 Strand 并提升"表达为 createStrand + promoteEpisode(OpId 引用)，不内嵌建树请求。
    /// </summary>
    public class PromoteEpisodeOpDto
    {
        public EpisodeRefDto Episode { get; set; } = new();

        /// <summary>EpisodeId 引用时必填；OpId 引用时由提交端补全。</summary>
        public long? ExpectedVersion { get; set; }

        public StrandRefDto Strand { get; set; } = new();

        /// <summary>要一并解决为 promoted 的 Probe（已有对象，按 UUIDv7）。</summary>
        public Guid? ProbeId { get; set; }

        public bool BindProbeMatcher { get; set; }
    }

    /// <summary>
    /// KnowledgeChangeSet 中的一个操作：Type 决定哪个 payload 字段生效，其余为 null。
    /// OpId 在 set 内唯一——错误定位、临时引用与提交结果映射都靠它。
    /// </summary>
    public class KnowledgeOperationDto
    {
        public string OpId { get; set; } = string.Empty;

        /// <summary>KnowledgeOpTypes 之一。</summary>
        public string Type { get; set; } = string.Empty;

        public CreateStrandOpDto? CreateStrand { get; set; }

        public UpdateStrandOpDto? UpdateStrand { get; set; }

        public MoveStrandOpDto? MoveStrand { get; set; }

        public EndStrandOpDto? EndStrand { get; set; }

        public BindMatcherOpDto? BindMatcher { get; set; }

        public MuteMatcherOpDto? MuteMatcher { get; set; }

        public CreateEpisodeOpDto? CreateEpisode { get; set; }

        public UpdateEpisodeOpDto? UpdateEpisode { get; set; }

        public RelateEpisodeOpDto? RelateEpisode { get; set; }

        public CreateProbeOpDto? CreateProbe { get; set; }

        public ResolveProbeOpDto? ResolveProbe { get; set; }

        public PromoteEpisodeOpDto? PromoteEpisode { get; set; }
    }

    /// <summary>
    /// 事务提交请求（ADR-031 §6）：用户确认后选中的操作，按序执行。未确认项不得出现在这里。
    /// 服务端重新校验全部领域不变量、Owner 与并发版本，不信任提案或前端编辑结果。
    /// </summary>
    public class CommitChangeSetRequest
    {
        public List<KnowledgeOperationDto> Operations { get; set; } = [];
    }

    /// <summary>单个操作的提交结果：真实 ID / 版本 / 路径，供 UI 替换临时 proposal 引用。</summary>
    public class OperationResultResponse
    {
        public string OpId { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        /// <summary>Strand 型操作（create/update/move/end/bind）的回读。</summary>
        public StrandResponse? Strand { get; set; }

        /// <summary>Episode 型操作（create/update/relate）的回读。</summary>
        public EpisodeResponse? Episode { get; set; }

        /// <summary>Probe 型操作（create/resolve）的回读。</summary>
        public ProbeResponse? Probe { get; set; }

        /// <summary>提升事务的回读。</summary>
        public PromoteEpisodeResponse? Promotion { get; set; }
    }

    /// <summary>整批成功的提交回读：与请求操作一一对应（含 OpId → 真实 UUIDv7 映射）。</summary>
    public class CommitChangeSetResponse
    {
        public List<OperationResultResponse> Results { get; set; } = [];
    }

    /// <summary>整批失败的提交错误：FailedOpId 定位具体操作（null = set 级校验失败），无部分写入。</summary>
    public class ChangeSetErrorResponse
    {
        /// <summary>失败操作的 OpId。null = 请求整体非法（空集 / OpId 重复等）。</summary>
        public string? FailedOpId { get; set; }

        public KnowledgeErrorResponse Error { get; set; } = new();
    }
}
