using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class GeneralizeUsageToActivitySegment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivitySegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<long>(type: "bigint", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdentityKey = table.Column<string>(type: "text", nullable: false),
                    AppId = table.Column<long>(type: "bigint", nullable: true),
                    Title = table.Column<string>(type: "text", nullable: true),
                    StartTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    Attributes = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivitySegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActivitySegments_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ActivitySegments_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // 历史 AppUsages 全部迁入,source='system'(ADR-017)。
            // Id 用 PG18 内置 uuidv7() 生成;IdentityKey 与 SystemIdentity.Key
            // 保持一致:lower(AppName) + '\n' + coalesce(Title, '')。
            migrationBuilder.Sql(@"
                INSERT INTO ""ActivitySegments""
                    (""Id"", ""DeviceId"", ""Source"", ""IdentityKey"", ""AppId"", ""Title"",
                     ""StartTime"", ""EndTime"", ""DurationSeconds"")
                SELECT
                    uuidv7(),
                    u.""DeviceId"",
                    'system',
                    lower(a.""Name"") || E'\n' || coalesce(u.""Title"", ''),
                    u.""AppId"",
                    u.""Title"",
                    u.""StartTime"",
                    u.""EndTime"",
                    u.""DurationSeconds""
                FROM ""AppUsages"" u
                JOIN ""Apps"" a ON a.""Id"" = u.""AppId"";
            ");

            migrationBuilder.DropTable(
                name: "AppUsages");

            migrationBuilder.CreateIndex(
                name: "IX_ActivitySegments_AppId",
                table: "ActivitySegments",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivitySegments_DeviceId",
                table: "ActivitySegments",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_ActivitySegments_DeviceId_Source_IdentityKey_EndTime",
                table: "ActivitySegments",
                columns: new[] { "DeviceId", "Source", "IdentityKey", "EndTime" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivitySegments_StartTime",
                table: "ActivitySegments",
                column: "StartTime");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppUsages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppId = table.Column<long>(type: "bigint", nullable: false),
                    DeviceId = table.Column<long>(type: "bigint", nullable: false),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    EndTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Title = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUsages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppUsages_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppUsages_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppUsages_AppId",
                table: "AppUsages",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUsages_DeviceId",
                table: "AppUsages",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUsages_DeviceId_AppId_EndTime",
                table: "AppUsages",
                columns: new[] { "DeviceId", "AppId", "EndTime" });

            migrationBuilder.CreateIndex(
                name: "IX_AppUsages_StartTime",
                table: "AppUsages",
                column: "StartTime");

            // 回滚:system 段迁回 AppUsages(bigint 自增主键重新生成);插件段(如有)丢弃。
            migrationBuilder.Sql(@"
                INSERT INTO ""AppUsages""
                    (""DeviceId"", ""AppId"", ""Title"", ""StartTime"", ""EndTime"", ""DurationSeconds"")
                SELECT ""DeviceId"", ""AppId"", ""Title"", ""StartTime"", ""EndTime"", ""DurationSeconds""
                FROM ""ActivitySegments""
                WHERE ""Source"" = 'system';
            ");

            migrationBuilder.DropTable(
                name: "ActivitySegments");
        }
    }
}
