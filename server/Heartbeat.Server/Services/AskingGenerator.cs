using System.Text;
using System.Text.Json;
using Heartbeat.Core.DTOs.Knowledge;

namespace Heartbeat.Server.Services
{
    /// <summary>
    /// 发问的 few-shot 语境（ADR-029 §4 裁决日志当锚）：用户历史裁决的渲染行。
    /// 空 = 冷启动，判官按世界知识裸判（更保守）。
    /// </summary>
    public sealed record AskingContext(IReadOnlyList<string> BoundExamples, IReadOnlyList<string> MutedExamples);

    /// <summary>
    /// 发问判官（ADR-029 §4）：对当日 digest 的第二次独立 LLM 调用。
    /// 返回 null = 调用失败（不写缓存）；空列表 = 今天没有值得问的（合法结果，可缓存）。
    /// </summary>
    public interface IAskingGenerator
    {
        Task<IReadOnlyList<QuestionItemResponse>?> AskAsync(
            string digest, AskingContext context, CancellationToken ct = default);
    }

    /// <summary>prompt 构建与解析是纯函数（可测）；传输走 ChatCompletionClient。</summary>
    public class OpenAiCompatibleAskingGenerator(ChatCompletionClient client) : IAskingGenerator
    {
        /// <summary>
        /// 判官人格（ADR-029 §4）："什么是世界知识解释不了的"整体交给 LLM；
        /// 确定性层只供证据（digest + 注释 + 裁决日志）与裁剪（封顶、diff）。偏安静。
        /// </summary>
        private const string SystemPrompt =
            """
            你是 Heartbeat 的发问判官。下面是用户某一天的电脑活动摘要（digest），以及用户历史裁决记录。
            你的任务：找出摘要里**世界知识解释不了、大概率承载用户私有含义**的活动，以最多 3 个问题向用户求证。

            判断标准：
            - 知名网站/软件（bilibili、github、vscode、微信……）你自己就认识——不问。
            - 陌生域名、陌生进程、内网/localhost 应用、以及"多个已知工具的组合指向一件说不清的事"（如游戏 + 直播工具长时间并行）——值得问。
            - 摘要的时间结构是证据：长会话、反复出现、多轨并行的组合，比零散噪声更值得问。
            - 偏安静：没把握就不问，宁可空手。输出空数组完全合法。
            - "近 14 天高频出现"注释里的东西是常驻基础设施，默认不问。
            - "已知脉络"里已被用户确认的事不再问。
            - 历史裁决记录是用户的口味锚点："已绑定"示范值得问的样子，"已静音"示范别问的样子。

            每个问题附带一个 matcher（观测指纹提案），用于以后自动认出这类活动：
            - source："system"（桌面进程）或 "browser"（浏览器）
            - steps：观测读数路径谓词，每步 {"reading","op","value"}；
              system 读数："app"（进程/应用）、"title"（窗口标题）；browser 读数："url"、"tab_title"；
              op ∈ "equals" | "prefix" | "contains"
            - 默认只用最浅一步；只有当分解显示同一读数值下明显混着多件事时才加更深读数的细化步。

            严格输出 JSON 数组（可为空），不要输出任何其他文字：
            [{"question":"向用户提的一句问题","evidence":"你在摘要里看到的依据（时段+组合）","matcher":{"source":"system","steps":[{"reading":"app","op":"equals","value":"…"}]},"proposedName":"猜的名字（没把握则空串）","proposedGloss":"猜的一句话释义（同上）"}]
            """;

        private static readonly JsonSerializerOptions ParseOptions = new() { PropertyNameCaseInsensitive = true };

        public async Task<IReadOnlyList<QuestionItemResponse>?> AskAsync(
            string digest, AskingContext context, CancellationToken ct = default)
        {
            string content;
            try
            {
                content = await client.CompleteAsync(SystemPrompt, BuildUserPrompt(digest, context), ct);
            }
            catch (ChatCompletionException)
            {
                return null; // 失败不装死也不假装：无问题可出，不写缓存（ADR-029 §4）
            }
            return Parse(content);
        }

        /// <summary>
        /// digest 在前：同类调用重复发问时共享 provider 前缀缓存
        /// （叙事调用 system prompt 不同，跨出口共享受限于供应商的前缀语义）。
        /// </summary>
        public static string BuildUserPrompt(string digest, AskingContext context)
        {
            var sb = new StringBuilder();
            sb.AppendLine(digest);
            sb.AppendLine();
            sb.AppendLine("## 用户历史裁决");
            if (context.BoundExamples.Count == 0 && context.MutedExamples.Count == 0)
                sb.AppendLine("（暂无——按世界知识裸判，更保守。）");
            foreach (var b in context.BoundExamples)
                sb.AppendLine($"- 已绑定：{b}");
            foreach (var m in context.MutedExamples)
                sb.AppendLine($"- 已静音：{m}");
            return sb.ToString();
        }

        /// <summary>
        /// 宽容解析（纯函数）：剥代码围栏、取最外层数组；整体不可解析返回 null（视为失败），
        /// 个别条目无效（缺问题文本 / matcher 规范化失败）则剔除该条。
        /// </summary>
        public static IReadOnlyList<QuestionItemResponse>? Parse(string content)
        {
            var start = content.IndexOf('[');
            var end = content.LastIndexOf(']');
            if (start < 0 || end <= start) return null;

            List<RawItem?>? items;
            try
            {
                items = JsonSerializer.Deserialize<List<RawItem?>>(content[start..(end + 1)], ParseOptions);
            }
            catch (JsonException)
            {
                return null;
            }
            if (items == null) return null;

            var result = new List<QuestionItemResponse>();
            foreach (var item in items)
            {
                if (item?.Matcher == null || string.IsNullOrWhiteSpace(item.Question)) continue;
                if (MatcherNormalizer.Normalize(item.Matcher) is not { } matcher) continue;
                result.Add(new QuestionItemResponse
                {
                    Matcher = matcher,
                    Question = item.Question.Trim(),
                    Evidence = (item.Evidence ?? string.Empty).Trim(),
                    ProposedName = (item.ProposedName ?? string.Empty).Trim(),
                    ProposedGloss = (item.ProposedGloss ?? string.Empty).Trim(),
                });
            }
            return result;
        }

        private sealed class RawItem
        {
            public string? Question { get; set; }
            public string? Evidence { get; set; }
            public MatcherDto? Matcher { get; set; }
            public string? ProposedName { get; set; }
            public string? ProposedGloss { get; set; }
        }
    }
}
