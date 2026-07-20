using System.Text.Json;
using Heartbeat.Core.DTOs.Knowledge;

namespace Heartbeat.Server.Services
{
    /// <summary>已确认 Strand 的投影输入（ADR-029 §3）：名字 + 释义 + 指纹（Matcher 集合）。</summary>
    public sealed record KnownStrandInput(string Name, string Gloss, IReadOnlyList<MatcherDto> Matchers);

    /// <summary>
    /// Matcher 规范化（ADR-029 §3）：裁决提交的入口清洗——值 trim、Op 小写并校验、
    /// 无效步剔除、步骤排序去重。规范化后的 Matcher 序列化为确定性 JSON，
    /// 幂等比较（同 Matcher 换步骤顺序重复提交）收敛到同一行。
    /// </summary>
    public static class MatcherNormalizer
    {
        private static readonly HashSet<string> ValidOps =
            [MatcherOps.Equal, MatcherOps.Prefix, MatcherOps.Contains];

        /// <summary>清洗一个提交的 Matcher。无效（空 Source / 无有效步）返回 null。</summary>
        public static MatcherDto? Normalize(MatcherDto matcher)
        {
            var source = (matcher.Source ?? string.Empty).Trim();
            if (source.Length == 0) return null;

            var steps = (matcher.Steps ?? [])
                .Select(s => new MatcherStepDto
                {
                    Layer = s.Layer,
                    Reading = (s.Reading ?? string.Empty).Trim(),
                    Op = (s.Op ?? string.Empty).Trim().ToLowerInvariant(),
                    Value = (s.Value ?? string.Empty).Trim(),
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

    /// <summary>
    /// Matcher 求值（ADR-029 §3，纯函数）：路径谓词各步合取——每步须存在
    /// 同层同读数、且值满足谓词的读数。值比较不区分大小写（进程名/URL 宽容）。
    /// </summary>
    public static class MatcherEval
    {
        public static bool Hits(string source, IReadOnlyList<DepthReading> readings, MatcherDto matcher)
            => matcher.Source == source
               && matcher.Steps.Count > 0
               && matcher.Steps.All(step => readings.Any(r =>
                   r.Layer == step.Layer
                   && string.Equals(r.Reading, step.Reading, StringComparison.Ordinal)
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
