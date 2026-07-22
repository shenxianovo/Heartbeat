using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Services
{
    /// <summary>
    /// NormalizeMatcherIdentity 迁移的 C# 护航（幂等数据修复）：把存量 Matcher 行重过
    /// MatcherNormalizer，落成全小写 canonical 形；归一后撞身份的行保最早（Id 序）。
    /// 必须在 C# 做——canonical 字节由 System.Text.Json 的转义（非 ASCII → \uXXXX 大写十六进制）
    /// 与属性声明序决定，SQL 无法复现；用真 Normalizer/Codec 是唯一零漂移路径。
    /// 启动时随迁移之后调用：干净库一次空转即返回，成本 = 读一遍策展层小表。
    /// </summary>
    public static class KnowledgeIdentityBackfill
    {
        public static async Task RunAsync(AppDbContext db, CancellationToken ct = default)
        {
            var strandPlan = Plan(
                await db.StrandMatchers.OrderBy(m => m.Id).ToListAsync(ct),
                m => (object)m.StrandId, m => (m.Source, m.StepsJson));
            var mutedPlan = Plan(
                await db.MutedMatchers.OrderBy(m => m.Id).ToListAsync(ct),
                m => (object)m.OwnerId, m => (m.Source, m.StepsJson));

            // 两阶段提交规避唯一索引的瞬时撞行：keeper 的 canonical 形只可能与
            // 本组待删行的现值相撞，先删完再改写即安全。
            if (strandPlan.Remove.Count > 0 || mutedPlan.Remove.Count > 0)
            {
                db.StrandMatchers.RemoveRange(strandPlan.Remove);
                db.MutedMatchers.RemoveRange(mutedPlan.Remove);
                await db.SaveChangesAsync(ct);
            }

            foreach (var (row, source, stepsJson) in strandPlan.Rewrite)
            {
                row.Source = source;
                row.StepsJson = stepsJson;
            }
            foreach (var (row, source, stepsJson) in mutedPlan.Rewrite)
            {
                row.Source = source;
                row.StepsJson = stepsJson;
            }
            if (strandPlan.Rewrite.Count > 0 || mutedPlan.Rewrite.Count > 0)
                await db.SaveChangesAsync(ct);
        }

        /// <summary>
        /// 归一计划：Remove = 归一后身份重复（组内保最早）或无法解码的死行；
        /// Rewrite = 存储形需改写成 canonical 的行。scope 是身份的分组键（StrandId / OwnerId）。
        /// </summary>
        private static (List<T> Remove, List<(T Row, string Source, string StepsJson)> Rewrite) Plan<T>(
            List<T> rows, Func<T, object> scope, Func<T, (string Source, string StepsJson)> current)
            where T : class
        {
            var remove = new List<T>();
            var rewrite = new List<(T, string, string)>();
            var seen = new HashSet<(object, string, string)>();
            foreach (var row in rows)
            {
                var (source, stepsJson) = current(row);
                var normalized = MatcherNormalizer.Normalize(new MatcherDto
                {
                    Source = source,
                    Steps = MatcherCodec.Deserialize(stepsJson),
                });
                if (normalized == null)
                {
                    remove.Add(row); // 解码失败/无有效步：无法参与匹配的死行
                    continue;
                }

                var canonical = MatcherCodec.Serialize(normalized.Steps);
                if (!seen.Add((scope(row), normalized.Source, canonical)))
                {
                    remove.Add(row);
                    continue;
                }

                if (source != normalized.Source || stepsJson != canonical)
                    rewrite.Add((row, normalized.Source, canonical));
            }
            return (remove, rewrite);
        }
    }
}
