using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <summary>
    /// ADR-031 issue 03：Recap 新增可空 KnowledgeHash——生成时实际使用的日期知识投影标识。
    /// 旧行保持 null（惰性视为可重新生成），不批量回填、不在迁移中调 LLM。
    /// </summary>
    public partial class RecapKnowledgeProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KnowledgeHash",
                table: "Recaps",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KnowledgeHash",
                table: "Recaps");
        }
    }
}
