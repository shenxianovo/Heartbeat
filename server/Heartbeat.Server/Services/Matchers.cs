using System.Text.Json;
using Heartbeat.Core.DTOs.Knowledge;

namespace Heartbeat.Server.Services
{
    /// <summary>已确认 Strand 的投影输入（ADR-029 §3）：名字 + 释义 + 指纹（Matcher 集合）。</summary>
    public sealed record KnownStrandInput(string Name, string Gloss, IReadOnlyList<MatcherDto> Matchers);

    /// <summary>
    /// Matcher 规范化（ADR-029 §3）：裁决提交的入口清洗——全字段 trim + 小写、Op 校验、
    /// 无效步剔除、步骤排序去重。canonical 小写形即裁决身份：身份判等（唯一索引 / Mute 幂等 /
    /// 读时 diff）与 MatcherEval 的大小写不敏感命中语义是同一把尺子——
    /// 否则"别再问"对着字符串承诺、对着观测事实食言（Code.exe / code.exe 双身份）。
    /// </summary>
    public static class MatcherNormalizer
    {
        private static readonly HashSet<string> ValidOps =
            [MatcherOps.Equal, MatcherOps.Prefix, MatcherOps.Contains];

        /// <summary>清洗一个提交的 Matcher。无效（空 Source / 无有效步）返回 null。</summary>
        public static MatcherDto? Normalize(MatcherDto matcher)
        {
            var source = (matcher.Source ?? string.Empty).Trim().ToLowerInvariant();
            if (source.Length == 0) return null;

            var steps = (matcher.Steps ?? [])
                .Select(s => new MatcherStepDto
                {
                    Layer = s.Layer,
                    Reading = (s.Reading ?? string.Empty).Trim().ToLowerInvariant(),
                    Op = (s.Op ?? string.Empty).Trim().ToLowerInvariant(),
                    Value = (s.Value ?? string.Empty).Trim().ToLowerInvariant(),
                })
                .Where(s => s.Layer >= 1 && s.Reading.Length > 0 && s.Value.Length > 0 && ValidOps.Contains(s.Op))
                .OrderBy(s => s.Layer)
                .ThenBy(s => s.Reading, StringComparer.Ordinal)
                .ThenBy(s => s.Op, StringComparer.Ordinal)
                .ThenBy(s => s.Value, StringComparer.Ordinal)
                .DistinctBy(s => (s.Layer, s.Reading, s.Op, s.Value))
                .ToList();

            return steps.Count == 0 ? null : new MatcherDto { Source = source, Steps = steps };
        }
    }

    /// <summary>StepsJson 编解码：规范化步骤序列 ↔ 确定性 JSON（属性序即声明序，无缩进）。</summary>
    public static class MatcherCodec
    {
        public static string Serialize(IReadOnlyList<MatcherStepDto> steps)
            => JsonSerializer.Serialize(steps);

        public static List<MatcherStepDto> Deserialize(string stepsJson)
        {
            try
            {
                return JsonSerializer.Deserialize<List<MatcherStepDto>>(stepsJson) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    /// <summary>Matcher 的人类可读渲染：few-shot 裁决日志行与调试用。</summary>
    public static class MatcherRender
    {
        public static string Describe(string source, IReadOnlyList<MatcherStepDto> steps)
            => $"{source}: {string.Join(" ∧ ", steps.Select(s => $"L{s.Layer} {s.Reading} {s.Op} \"{s.Value}\""))}";
    }

    /// <summary>
    /// Matcher 求值（ADR-029 §3，纯函数）：路径谓词各步合取——每步须存在
    /// 同层同读数、且值满足谓词的读数。Source / 读数名 / 值全部大小写不敏感：
    /// 命中等价类 = 裁决身份等价类（MatcherNormalizer canonical 小写形），一把尺子。
    /// </summary>
    public static class MatcherEval
    {
        public static bool Hits(string source, IReadOnlyList<DepthReading> readings, MatcherDto matcher)
            => string.Equals(matcher.Source, source, StringComparison.OrdinalIgnoreCase)
               && matcher.Steps.Count > 0
               && matcher.Steps.All(step => readings.Any(r =>
                   r.Layer == step.Layer
                   && string.Equals(r.Reading, step.Reading, StringComparison.OrdinalIgnoreCase)
                   && Match(step.Op, r.Value, step.Value)));

        private static bool Match(string op, string actual, string expected) => op switch
        {
            MatcherOps.Equal => actual.Equals(expected, StringComparison.OrdinalIgnoreCase),
            MatcherOps.Prefix => actual.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            MatcherOps.Contains => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}
