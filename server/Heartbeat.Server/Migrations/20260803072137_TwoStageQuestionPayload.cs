using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class TwoStageQuestionPayload : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PayloadVersion",
                table: "DailyQuestionSets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // 旧单阶段问题卡与两阶段协议不兼容：清空缓存，不迁移机器提案（ADR-031 迁移）。
            // 读取路径同时按 PayloadVersion 判失效——留存副本（回滚/备份）也不会被当作可提交表单。
            migrationBuilder.Sql("""DELETE FROM "DailyQuestionSets";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PayloadVersion",
                table: "DailyQuestionSets");
        }
    }
}
