using Heartbeat.Core;
using Heartbeat.Core.DTOs.Knowledge;
using Heartbeat.Server.Entities;
using Heartbeat.Server.Services;
using Heartbeat.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Server.Tests.Services;

/// <summary>
/// 存量身份重归一（NormalizeMatcherIdentity 迁移的 C# 护航）：老尺子写入的行
/// 落成 canonical 小写形，归一后撞身份保最早行——用户亲口事实零丢失。
/// </summary>
[Collection("postgres")]
public class KnowledgeIdentityBackfillTests(PostgresContainerFixture fixture) : PostgresTestBase(fixture)
{
    /// <summary>老尺子写入的 StepsJson（含已退役的 Layer 字段与原大小写）——backfill 需剥层 + 归一。</summary>
    private static string LegacyStepsJson(string value) =>
        $$"""[{"Layer":1,"Reading":"app","Op":"equals","Value":"{{value}}"}]""";

    private static string Canonical(string value) =>
        MatcherCodec.Serialize(MatcherNormalizer.Normalize(new MatcherDto
        {
            Source = ActivitySources.System,
            Steps = [new() { Reading = "app", Op = MatcherOps.Equal, Value = value }]
        })!.Steps);

    [Fact]
    public async Task Run_RewritesLegacyCasing_AndDedupesCollidingIdentities()
    {
        using (var db = CreateDbContext())
        {
            var strand = new Strand
            {
                Id = Guid.CreateVersion7(),
                OwnerId = "user-1",
                Name = "HyperFrames",
                Gloss = "动效框架",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            strand.Members.Add(new StrandMatcher { Source = "system", StepsJson = Canonical("code.exe") });
            strand.Members.Add(new StrandMatcher { Source = "system", StepsJson = LegacyStepsJson("Code.EXE") }); // 归一后与上行撞身份
            strand.Members.Add(new StrandMatcher { Source = "system", StepsJson = LegacyStepsJson("Blender.exe") }); // 只需改写
            db.Strands.Add(strand);

            db.MutedMatchers.Add(new MutedMatcher
            {
                OwnerId = "user-1",
                Source = "system",
                StepsJson = LegacyStepsJson("WeChat.EXE"), // 改写成 canonical
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.MutedMatchers.Add(new MutedMatcher
            {
                OwnerId = "user-1",
                Source = "system",
                StepsJson = "not-json", // 无法参与匹配的死行 → 删除
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using (var db = CreateDbContext())
        {
            await KnowledgeIdentityBackfill.RunAsync(db);
        }

        using (var db = CreateDbContext())
        {
            var members = await db.StrandMatchers.OrderBy(m => m.Id).ToListAsync();
            Assert.Equal(2, members.Count); // 撞身份保最早，Blender 行改写保留
            Assert.Equal(Canonical("code.exe"), members[0].StepsJson);
            Assert.Equal(Canonical("blender.exe"), members[1].StepsJson);

            var muted = Assert.Single(await db.MutedMatchers.ToListAsync());
            Assert.Equal(Canonical("wechat.exe"), muted.StepsJson);

            // 幂等：再跑一遍无事发生
            await KnowledgeIdentityBackfill.RunAsync(db);
            Assert.Equal(2, (await db.StrandMatchers.ToListAsync()).Count);
        }
    }
}
