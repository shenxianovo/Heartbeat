using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddAppIconOwnerId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 旧图标行没有 owner，无法从全局 App 反推归属；直接清空。
            // Agent 每次进程启动会重传（IconUploadService 去重表是进程内的），几分钟内自愈。
            migrationBuilder.Sql("DELETE FROM \"AppIcons\";");

            migrationBuilder.DropIndex(
                name: "IX_AppIcons_AppId",
                table: "AppIcons");

            migrationBuilder.AddColumn<string>(
                name: "OwnerId",
                table: "AppIcons",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_AppIcons_AppId",
                table: "AppIcons",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_AppIcons_OwnerId_AppId",
                table: "AppIcons",
                columns: new[] { "OwnerId", "AppId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AppIcons_AppId",
                table: "AppIcons");

            migrationBuilder.DropIndex(
                name: "IX_AppIcons_OwnerId_AppId",
                table: "AppIcons");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "AppIcons");

            migrationBuilder.CreateIndex(
                name: "IX_AppIcons_AppId",
                table: "AppIcons",
                column: "AppId",
                unique: true);
        }
    }
}
