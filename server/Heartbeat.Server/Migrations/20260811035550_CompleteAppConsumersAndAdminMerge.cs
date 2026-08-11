using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class CompleteAppConsumersAndAdminMerge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CurrentAppIdentityId",
                table: "Devices",
                type: "bigint",
                nullable: true);

            // expand：旧 presence 只保存 Windows AppName 展示串。只按规范化 identity
            // 精确回填；DisplayName 不是平台观测身份，不能用来猜测全局产品映射（ADR-034）。
            migrationBuilder.Sql(
                """
                UPDATE "Devices" AS device
                SET "CurrentAppIdentityId" = (
                    SELECT identity."Id"
                    FROM "AppIdentities" AS identity
                    WHERE identity."Key" = CASE
                            WHEN lower(trim(device."CurrentApp")) = '__away__' THEN 'sys:away'
                            ELSE 'win:' || lower(regexp_replace(trim(device."CurrentApp"), '\.exe$', '', 'i'))
                          END
                    LIMIT 1
                )
                WHERE nullif(trim(device."CurrentApp"), '') IS NOT NULL;
                """);

            migrationBuilder.CreateTable(
                name: "AppMergeReceipts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SourceAppKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TargetAppKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TargetAppId = table.Column<long>(type: "bigint", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResponseJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppMergeReceipts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_CurrentAppIdentityId",
                table: "Devices",
                column: "CurrentAppIdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_AppMergeReceipts_SourceAppKey_TargetAppKey",
                table: "AppMergeReceipts",
                columns: new[] { "SourceAppKey", "TargetAppKey" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Devices_AppIdentities_CurrentAppIdentityId",
                table: "Devices",
                column: "CurrentAppIdentityId",
                principalTable: "AppIdentities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Devices_AppIdentities_CurrentAppIdentityId",
                table: "Devices");

            migrationBuilder.DropTable(
                name: "AppMergeReceipts");

            migrationBuilder.DropIndex(
                name: "IX_Devices_CurrentAppIdentityId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "CurrentAppIdentityId",
                table: "Devices");
        }
    }
}
