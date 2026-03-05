using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class RefactorAppAndDeviceIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. 创建 Apps 表
            migrationBuilder.CreateTable(
                name: "Apps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Apps", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Apps_Name",
                table: "Apps",
                column: "Name",
                unique: true);

            // 2. 从现有数据填充 Apps 表
            migrationBuilder.Sql(@"
                INSERT INTO ""Apps"" (""Name"")
                SELECT DISTINCT ""AppName"" FROM (
                    SELECT ""AppName"" FROM ""AppUsages""
                    UNION
                    SELECT ""AppName"" FROM ""AppIcons""
                ) AS all_apps
                WHERE ""AppName"" IS NOT NULL AND ""AppName"" != '';
            ");

            // 3. 添加新列（先设为 nullable）
            migrationBuilder.AddColumn<long>(
                name: "DeviceId",
                table: "AppUsages",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AppId",
                table: "AppUsages",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AppId",
                table: "AppIcons",
                type: "bigint",
                nullable: true);

            // 4. 从旧数据填充新列
            migrationBuilder.Sql(@"
                UPDATE ""AppUsages"" u
                SET ""DeviceId"" = d.""Id""
                FROM ""Devices"" d
                WHERE u.""DeviceName"" = d.""DeviceName"";
            ");

            migrationBuilder.Sql(@"
                UPDATE ""AppUsages"" u
                SET ""AppId"" = a.""Id""
                FROM ""Apps"" a
                WHERE u.""AppName"" = a.""Name"";
            ");

            migrationBuilder.Sql(@"
                UPDATE ""AppIcons"" i
                SET ""AppId"" = a.""Id""
                FROM ""Apps"" a
                WHERE i.""AppName"" = a.""Name"";
            ");

            // 5. 删除无法关联的孤立记录
            migrationBuilder.Sql(@"DELETE FROM ""AppUsages"" WHERE ""DeviceId"" IS NULL OR ""AppId"" IS NULL;");
            migrationBuilder.Sql(@"DELETE FROM ""AppIcons"" WHERE ""AppId"" IS NULL;");

            // 6. 将新列设为 NOT NULL
            migrationBuilder.Sql(@"ALTER TABLE ""AppUsages"" ALTER COLUMN ""DeviceId"" SET NOT NULL;");
            migrationBuilder.Sql(@"ALTER TABLE ""AppUsages"" ALTER COLUMN ""AppId"" SET NOT NULL;");
            migrationBuilder.Sql(@"ALTER TABLE ""AppIcons"" ALTER COLUMN ""AppId"" SET NOT NULL;");

            // 7. 删除旧索引
            migrationBuilder.DropIndex(
                name: "IX_AppUsages_DeviceName",
                table: "AppUsages");

            migrationBuilder.DropIndex(
                name: "IX_AppUsages_DeviceName_AppName_EndTime",
                table: "AppUsages");

            migrationBuilder.DropIndex(
                name: "IX_AppIcons_AppName",
                table: "AppIcons");

            // 8. 删除旧列
            migrationBuilder.DropColumn(
                name: "AppName",
                table: "AppUsages");

            migrationBuilder.DropColumn(
                name: "DeviceName",
                table: "AppUsages");

            migrationBuilder.DropColumn(
                name: "AppName",
                table: "AppIcons");

            // 9. 创建新索引
            migrationBuilder.CreateIndex(
                name: "IX_AppUsages_DeviceId",
                table: "AppUsages",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUsages_AppId",
                table: "AppUsages",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUsages_DeviceId_AppId_EndTime",
                table: "AppUsages",
                columns: new[] { "DeviceId", "AppId", "EndTime" });

            migrationBuilder.CreateIndex(
                name: "IX_AppIcons_AppId",
                table: "AppIcons",
                column: "AppId",
                unique: true);

            // 10. 添加外键约束
            migrationBuilder.AddForeignKey(
                name: "FK_AppIcons_Apps_AppId",
                table: "AppIcons",
                column: "AppId",
                principalTable: "Apps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AppUsages_Apps_AppId",
                table: "AppUsages",
                column: "AppId",
                principalTable: "Apps",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AppUsages_Devices_DeviceId",
                table: "AppUsages",
                column: "DeviceId",
                principalTable: "Devices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AppIcons_Apps_AppId",
                table: "AppIcons");

            migrationBuilder.DropForeignKey(
                name: "FK_AppUsages_Apps_AppId",
                table: "AppUsages");

            migrationBuilder.DropForeignKey(
                name: "FK_AppUsages_Devices_DeviceId",
                table: "AppUsages");

            migrationBuilder.DropTable(
                name: "Apps");

            migrationBuilder.DropIndex(
                name: "IX_AppUsages_AppId",
                table: "AppUsages");

            migrationBuilder.DropIndex(
                name: "IX_AppUsages_DeviceId",
                table: "AppUsages");

            migrationBuilder.DropIndex(
                name: "IX_AppUsages_DeviceId_AppId_EndTime",
                table: "AppUsages");

            migrationBuilder.DropIndex(
                name: "IX_AppIcons_AppId",
                table: "AppIcons");

            migrationBuilder.DropColumn(
                name: "AppId",
                table: "AppUsages");

            migrationBuilder.DropColumn(
                name: "DeviceId",
                table: "AppUsages");

            migrationBuilder.DropColumn(
                name: "AppId",
                table: "AppIcons");

            migrationBuilder.AddColumn<string>(
                name: "AppName",
                table: "AppUsages",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "DeviceName",
                table: "AppUsages",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AppName",
                table: "AppIcons",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_AppUsages_DeviceName",
                table: "AppUsages",
                column: "DeviceName");

            migrationBuilder.CreateIndex(
                name: "IX_AppUsages_DeviceName_AppName_EndTime",
                table: "AppUsages",
                columns: new[] { "DeviceName", "AppName", "EndTime" });

            migrationBuilder.CreateIndex(
                name: "IX_AppIcons_AppName",
                table: "AppIcons",
                column: "AppName",
                unique: true);
        }
    }
}
