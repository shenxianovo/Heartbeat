using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <summary>
    /// Matcher 身份归一（一把尺子：裁决身份判等 = MatcherEval 命中等价类，全小写 canonical）。
    /// 本迁移处理纯文本可做的一半：Source 小写、同 Owner 大小写变体 Strand 合并（成员改挂最早行、
    /// gloss 空则从被并行补位、UpdatedAt 取最大——用户亲口事实零丢失）、收敛键换函数式唯一索引
    /// (OwnerId, lower(Name))。StepsJson 内 Reading/Value 的小写化由启动护航 KnowledgeIdentityBackfill
    /// 完成——canonical 字节由 System.Text.Json 的转义（\uXXXX 大写十六进制）与属性序决定，SQL 无法复现。
    /// </summary>
    public partial class NormalizeMatcherIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Strands_OwnerId_Name",
                table: "Strands");

            // Source 小写（ActivitySources 常量本就小写，此处防御性收底）。
            migrationBuilder.Sql("""
                UPDATE "StrandMatchers" SET "Source" = lower("Source") WHERE "Source" <> lower("Source");
                UPDATE "MutedMatchers" SET "Source" = lower("Source") WHERE "Source" <> lower("Source");
                """);

            // 同 Owner 下大小写变体 Strand 合并到最早行（CreatedAt, Id 序）。
            // 1) 成员改挂 keeper；keeper 已有同身份成员的跳过（随 loser 级联删除）。
            migrationBuilder.Sql("""
                UPDATE "StrandMatchers" m SET "StrandId" = k."Id"
                FROM "Strands" s
                JOIN "Strands" k
                  ON k."OwnerId" = s."OwnerId"
                 AND lower(k."Name") = lower(s."Name")
                 AND (k."CreatedAt", k."Id") < (s."CreatedAt", s."Id")
                WHERE m."StrandId" = s."Id"
                  AND NOT EXISTS (
                      SELECT 1 FROM "Strands" k2
                      WHERE k2."OwnerId" = s."OwnerId"
                        AND lower(k2."Name") = lower(s."Name")
                        AND (k2."CreatedAt", k2."Id") < (k."CreatedAt", k."Id"))
                  AND NOT EXISTS (
                      SELECT 1 FROM "StrandMatchers" km
                      WHERE km."StrandId" = k."Id"
                        AND km."Source" = m."Source"
                        AND km."StepsJson" = m."StepsJson");
                """);

            // 2) keeper 的 gloss 空则从最早的非空 loser 补位；UpdatedAt 取组内最大。
            migrationBuilder.Sql("""
                UPDATE "Strands" k SET
                    "Gloss" = COALESCE(
                        NULLIF(k."Gloss", ''),
                        (SELECT l."Gloss" FROM "Strands" l
                         WHERE l."OwnerId" = k."OwnerId" AND lower(l."Name") = lower(k."Name")
                           AND l."Id" <> k."Id" AND l."Gloss" <> ''
                         ORDER BY l."CreatedAt", l."Id" LIMIT 1),
                        k."Gloss"),
                    "UpdatedAt" = (SELECT max(l."UpdatedAt") FROM "Strands" l
                                   WHERE l."OwnerId" = k."OwnerId" AND lower(l."Name") = lower(k."Name"))
                WHERE NOT EXISTS (
                          SELECT 1 FROM "Strands" e
                          WHERE e."OwnerId" = k."OwnerId" AND lower(e."Name") = lower(k."Name")
                            AND (e."CreatedAt", e."Id") < (k."CreatedAt", k."Id"))
                  AND EXISTS (
                          SELECT 1 FROM "Strands" l
                          WHERE l."OwnerId" = k."OwnerId" AND lower(l."Name") = lower(k."Name")
                            AND l."Id" <> k."Id");
                """);

            // 3) 删除 loser 行（残余成员随 FK 级联删除）。
            migrationBuilder.Sql("""
                DELETE FROM "Strands" s
                WHERE EXISTS (
                    SELECT 1 FROM "Strands" k
                    WHERE k."OwnerId" = s."OwnerId"
                      AND lower(k."Name") = lower(s."Name")
                      AND (k."CreatedAt", k."Id") < (s."CreatedAt", s."Id"));
                """);

            // 收敛键：大小写变体不裂出第二条 Strand（EF 不支持表达式索引，SQL 直建）。
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_Strands_OwnerId_LowerName" ON "Strands" ("OwnerId", lower("Name"));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX "IX_Strands_OwnerId_LowerName";""");

            // 数据变换（Source 小写 / Strand 合并）不可逆，仅恢复索引形状。
            migrationBuilder.CreateIndex(
                name: "IX_Strands_OwnerId_Name",
                table: "Strands",
                columns: new[] { "OwnerId", "Name" },
                unique: true);
        }
    }
}
