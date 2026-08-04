using System.Text;
using System.Text.Json;
using Heartbeat.Core.DTOs.Knowledge;

namespace Heartbeat.Server.Services
{
    /// <summary>proposal 语境里的一个已有 Strand：UUIDv7 + path + 日期（同名消歧）+ 读取时版本。</summary>
    public sealed record ProposalStrand(
        Guid Id, IReadOnlyList<string> Path, string Gloss,
        DateOnly? StartedOn, DateOnly? EndedOn, long Version);

    /// <summary>proposal 语境里的一个已有 Episode（目标日期 ∪ recurrence 问题的源 Episode）。</summary>
    public sealed record ProposalEpisode(Guid Id, DateOnly LocalDate, string Text, long Version);

    /// <summary>proposal 语境里的一个活跃 Probe。</summary>
    public sealed record ProposalProbe(Guid Id, Guid EpisodeId);

    /// <summary>
    /// 整理者的知识语境（ADR-031 §6）：服务端从库里取的既有对象快照——LLM 只能引用这里
    /// 出现过的 UUIDv7，sanitizer 按同一份语境剔除虚构/越权引用并盖读取时版本。
    /// </summary>
    public sealed record ProposalContext(
        IReadOnlyList<ProposalStrand> Strands,
        IReadOnlyList<ProposalEpisode> Episodes,
        IReadOnlyList<ProposalProbe> Probes,
        DateOnly LocalDate,
        string ReadingVocabulary = "",
        IReadOnlyDictionary<string, string>? LabelsOrNull = null)
    {
        /// <summary>读数展示名词典（ADR-030 §7）：proposal 响应里 Matcher 渲染用。</summary>
        public IReadOnlyDictionary<string, string> Labels => LabelsOrNull ?? new Dictionary<string, string>();
    }

    /// <summary>LLM 原始提案信封：解析层只管形状，引用合法性归 ProposalSanitizer。</summary>
    public sealed class RawKnowledgeProposal
    {
        public string? Explanation { get; set; }
        public List<RawKnowledgeOperation?>? Operations { get; set; }
        public List<string?>? Suggestions { get; set; }
    }

    /// <summary>
    /// LLM 原始操作（扁平 union）：Type 决定哪些字段有意义。ID 一律字符串——
    /// 解析不判合法性，虚构/畸形引用在 sanitizer 统一剔除并出警告。
    /// </summary>
    public sealed class RawKnowledgeOperation
    {
        public string? OpId { get; set; }
        public string? Type { get; set; }

        public string? Name { get; set; }
        public string? Gloss { get; set; }
        public string? Text { get; set; }
        public string? Resolution { get; set; }

        public string? StrandId { get; set; }
        public string? StrandOpId { get; set; }
        public string? ParentStrandId { get; set; }
        public string? ParentOpId { get; set; }
        public string? NewParentStrandId { get; set; }
        public string? NewParentOpId { get; set; }
        public string? EpisodeId { get; set; }
        public string? EpisodeOpId { get; set; }
        public string? RelatedStrandId { get; set; }
        public string? RelatedOpId { get; set; }
        public string? ProbeId { get; set; }

        public string? StartedOn { get; set; }
        public string? EndedOn { get; set; }
        public string? LocalDate { get; set; }
        public string? ApproximateStart { get; set; }
        public string? ApproximateEnd { get; set; }

        public MatcherDto? Matcher { get; set; }
        public List<MatcherDto>? Members { get; set; }
        public bool? BindProbeMatcher { get; set; }
    }

    /// <summary>
    /// 教学整理者（ADR-031 §6 第二阶段）：用户的自然语言解释 → 原始结构化提案。
    /// 只产提案，不持有数据库写权限；返回 null = 调用失败（端点映射 502，无副作用）。
    /// </summary>
    public interface IProposalGenerator
    {
        /// <summary>主动发问入口：解释一张服务端发出的证据卡。</summary>
        Task<RawKnowledgeProposal?> ProposeAsync(
            AskingQuestionResponse question, string answer, ProposalContext context,
            CancellationToken ct = default);

        /// <summary>
        /// Recap 纠正入口（issue 06）：解释用户对某日回顾的纠正。证据是该日的活动摘要
        /// （与叙事同一份 digest）——不喂 Recap 散文，事实只来自目标日观察与用户的话。
        /// </summary>
        Task<RawKnowledgeProposal?> ProposeCorrectionAsync(
            string digest, string correction, ProposalContext context,
            CancellationToken ct = default);
    }

    /// <summary>prompt 构建与解析是纯函数（可测）；传输走 ChatCompletionClient。</summary>
    public class OpenAiCompatibleProposalGenerator(ChatCompletionClient client) : IProposalGenerator
    {
        /// <summary>
        /// 整理者人格（ADR-031 §6）：把用户的自然语言解释翻译成可编辑的操作清单。
        /// 明确区分解释 / 操作 / 建议；已有对象只能按语境清单里的 id 引用——
        /// sanitizer 会把清单之外的引用整个剔除，名称绝不用于绑定。
        /// 读数词汇段占位 {{VOCAB}} 由生效声明渲染（ADR-030 §7）。
        /// 两个入口（证据卡回答 / Recap 纠正）共享领域模型与操作词汇，只换开场语境。
        /// </summary>
        private const string QuestionIntro =
            """
            你是 Heartbeat 的知识整理者。Heartbeat 观察用户的电脑活动；刚才它向用户展示了一张"活动证据卡"提问，用户用自然语言回答了。你的任务：把用户的回答整理成一组结构化的知识操作提案，供用户逐项确认。你只提议，不写入——用户确认后才由系统提交。
            """;

        /// <summary>
        /// Recap 纠正入口的开场（issue 06）：明确不改散文——纠正写入知识，回顾随后重新生成。
        /// </summary>
        private const string CorrectionIntro =
            """
            你是 Heartbeat 的知识整理者。Heartbeat 观察用户的电脑活动，并为每一天生成回顾叙事；用户刚才对某一天的回顾提出了纠正——指出遗漏、错误的关联，或希望 Heartbeat 长期记住的私人语境。你的任务：把用户的纠正整理成一组结构化的知识操作提案，供用户逐项确认。你只提议，不写入——用户确认后才由系统提交，那一天的回顾随后会用更新后的知识重新生成。不要试图直接改写回顾文字：只属于那一天的事实用 createEpisode（localDate = 目标日期）；跨日期持续或用户希望长期记住的语境用 Strand；不确定是否会再发生的用 createEpisode + createProbe。
            """;

        private const string SharedPromptBody =
            """

            领域模型：
            - Strand（脉络）：跨日期延续的私人语境，组成严格单父级树（如"哔哩哔哩实习 → Hyperframes → 产品调研"）。有名字、一句话释义（gloss）、可选的近似起止日期（yyyy-MM-dd）。
            - Episode（片段事实）：某一天的一次具体发生，自由文本，可选近似起止时间，至多关联一个最具体的 Strand。
            - Matcher：观测指纹，命中时唤醒 Strand 知识。steps 每步 {"reading","op","value"}，op ∈ "equals" | "prefix" | "contains"。source 与读数词汇（浅 → 深）：
            {{VOCAB}}
            - RecurrenceProbe：挂在"还不确定会不会再发生"的 Episode 上的观察谓词，与 Matcher 同形。

            可用操作（type 及各自字段；引用已有对象一律用下方"已知知识"清单中的 id，绝不允许编造或用名字指代；引用本提案内新建的对象用其 opId）：
            - createStrand: name, gloss, parentStrandId 或 parentOpId（都省略 = 顶层）, startedOn?, endedOn?, members?（Matcher 数组）
            - updateStrand: strandId, name?, gloss?, startedOn?, endedOn?（省略的字段 = 保持现状）
            - moveStrand: strandId, newParentStrandId 或 newParentOpId（都省略 = 移到顶层）——仅用于纠正过去的错误理解
            - endStrand: strandId, endedOn
            - bindMatcher: strandId 或 strandOpId, matcher
            - muteMatcher: matcher——用户说"这不承载知识，别再问"
            - createEpisode: localDate (yyyy-MM-dd), text, approximateStart?, approximateEnd?（ISO 8601）, relatedStrandId 或 relatedOpId?
            - updateEpisode: episodeId, localDate?, text?, approximateStart?, approximateEnd?
            - relateEpisode: episodeId 或 episodeOpId, relatedStrandId 或 relatedOpId（都省略 = 解除关联）
            - createProbe: episodeId 或 episodeOpId, matcher——用户不确定这件事会不会再发生时
            - resolveProbe: probeId, resolution（"denied" = 确认一次性 / "muted" = 别再问）
            - promoteEpisode: episodeId 或 episodeOpId, strandId 或 strandOpId, probeId?, bindProbeMatcher?——把一次发生提升为持续脉络（新建脉络时先 createStrand 再用 strandOpId 引用）

            整理规则：
            - 明确一次性的行为：只 createEpisode（如有 Probe 在问，加 resolveProbe denied）。
            - 不确定是否持续：createEpisode + createProbe。
            - 明确持续：createStrand（或引用已有 Strand）+ bindMatcher；当天的具体发生可同时 createEpisode 关联它。
            - 用户嫌烦/说不重要：muteMatcher。
            - 层级、名字、日期照用户的话写；用户没说的不要编。宁可少提操作，把不确定的想法放进 suggestions。
            - explanation 用一两句复述你对用户回答的理解；suggestions 放无需保存的建议；不要把它们混进 operations。

            严格输出一个 JSON 对象，不要输出任何其他文字：
            {"explanation":"…","operations":[{"opId":"op1","type":"…",…}],"suggestions":["…"]}
            """;

        /// <summary>组装整理者 system prompt（纯函数）：词汇段注入模板。空词汇回落种子声明。</summary>
        public static string BuildSystemPrompt(string readingVocabulary)
            => InjectVocabulary(QuestionIntro + SharedPromptBody, readingVocabulary);

        /// <summary>Recap 纠正入口的 system prompt（纯函数）：同一领域模型与操作词汇，换开场语境。</summary>
        public static string BuildCorrectionSystemPrompt(string readingVocabulary)
            => InjectVocabulary(CorrectionIntro + SharedPromptBody, readingVocabulary);

        private static string InjectVocabulary(string template, string readingVocabulary)
            => template.Replace("{{VOCAB}}",
                string.IsNullOrWhiteSpace(readingVocabulary)
                    ? DepthTables.Seeds.DescribeForPrompt()
                    : readingVocabulary);

        private static readonly JsonSerializerOptions ParseOptions = new() { PropertyNameCaseInsensitive = true };

        public async Task<RawKnowledgeProposal?> ProposeAsync(
            AskingQuestionResponse question, string answer, ProposalContext context,
            CancellationToken ct = default)
        {
            string content;
            try
            {
                content = await client.CompleteAsync(
                    BuildSystemPrompt(context.ReadingVocabulary),
                    BuildUserPrompt(question, answer, context), ct);
            }
            catch (ChatCompletionException)
            {
                return null; // 失败无副作用：proposal 阶段本就零写入
            }
            return Parse(content);
        }

        public async Task<RawKnowledgeProposal?> ProposeCorrectionAsync(
            string digest, string correction, ProposalContext context,
            CancellationToken ct = default)
        {
            string content;
            try
            {
                content = await client.CompleteAsync(
                    BuildCorrectionSystemPrompt(context.ReadingVocabulary),
                    BuildCorrectionUserPrompt(digest, correction, context), ct);
            }
            catch (ChatCompletionException)
            {
                return null; // 失败无副作用：proposal 阶段本就零写入
            }
            return Parse(content);
        }

        /// <summary>用户 prompt（纯函数）：证据卡 + 已知知识清单（带 UUIDv7）+ 用户回答。</summary>
        public static string BuildUserPrompt(
            AskingQuestionResponse question, string answer, ProposalContext context)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## 证据卡（{context.LocalDate:yyyy-MM-dd}）");
            sb.AppendLine($"问题：{question.Question}");
            if (question.ApproximateStart is { } start && question.ApproximateEnd is { } end)
                sb.AppendLine($"大概时段：{start:HH:mm}–{end:HH:mm}（UTC）");
            sb.AppendLine($"锚定指纹：{MatcherRender.Describe(question.Matcher.Source, question.Matcher.Steps)}");
            if (question.Observations.Count > 0)
            {
                sb.AppendLine("时段内观察：");
                foreach (var o in question.Observations)
                {
                    var detail = string.IsNullOrEmpty(o.Detail) ? "" : $"（{o.Detail}）";
                    var hit = o.MatchesFingerprint ? "，命中指纹" : "";
                    sb.AppendLine($"- [{o.Source}] {o.Value}{detail} — {(int)(o.Seconds / 60)}分{hit}");
                }
            }
            if (question.Kind == AskingQuestionKinds.Recurrence && question.EpisodeText != null)
                sb.AppendLine($"这个问题来自复现探针：用户 {question.EpisodeDate:yyyy-MM-dd} 确认过「{question.EpisodeText}」（episodeId: {question.EpisodeId}，probeId: {question.ProbeId}），现在相似活动又出现了。");

            AppendKnownKnowledge(sb, context);

            sb.AppendLine();
            sb.AppendLine("## 用户的回答");
            sb.AppendLine(answer.Trim());
            return sb.ToString();
        }

        /// <summary>
        /// Recap 纠正入口的 user prompt（纯函数，issue 06）：目标日活动摘要（与叙事同一份
        /// digest）+ 已知知识清单 + 用户的纠正。不含 Recap 散文——散文只是显示上下文，
        /// 事实证据来自目标日观察与用户的话。
        /// </summary>
        public static string BuildCorrectionUserPrompt(string digest, string correction, ProposalContext context)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## 被纠正的回顾日期：{context.LocalDate:yyyy-MM-dd}");
            sb.AppendLine();
            sb.AppendLine("## 那一天的活动摘要（系统观察，事实来源）");
            sb.AppendLine(digest.Trim());

            AppendKnownKnowledge(sb, context);

            sb.AppendLine();
            sb.AppendLine("## 用户对那一天回顾的纠正");
            sb.AppendLine(correction.Trim());
            return sb.ToString();
        }

        /// <summary>已知知识清单段（两个入口共用）：全部 Strand（path/日期/id）+ 语境内 Episode。</summary>
        private static void AppendKnownKnowledge(StringBuilder sb, ProposalContext context)
        {
            sb.AppendLine();
            sb.AppendLine("## 已知知识（引用已有对象只能用这里的 id）");
            if (context.Strands.Count == 0) sb.AppendLine("（还没有任何 Strand。）");
            foreach (var s in context.Strands)
            {
                var dates = s.StartedOn == null && s.EndedOn == null
                    ? "" : $"，{s.StartedOn?.ToString("yyyy-MM-dd") ?? "?"}–{s.EndedOn?.ToString("yyyy-MM-dd") ?? "至今"}";
                var gloss = string.IsNullOrWhiteSpace(s.Gloss) ? "" : $"：{s.Gloss}";
                sb.AppendLine($"- Strand {string.Join(" → ", s.Path)}{gloss}（id: {s.Id}{dates}）");
            }
            foreach (var e in context.Episodes)
                sb.AppendLine($"- Episode {e.LocalDate:yyyy-MM-dd}「{e.Text}」（id: {e.Id}）");
        }

        /// <summary>
        /// 宽容解析（纯函数）：剥围栏取最外层对象；整体不可解析返回 null（视为失败）。
        /// 单条操作的合法性不在此判——sanitizer 逐条裁决并出警告。
        /// </summary>
        public static RawKnowledgeProposal? Parse(string content)
        {
            var start = content.IndexOf('{');
            var end = content.LastIndexOf('}');
            if (start < 0 || end <= start) return null;

            try
            {
                return JsonSerializer.Deserialize<RawKnowledgeProposal>(content[start..(end + 1)], ParseOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
