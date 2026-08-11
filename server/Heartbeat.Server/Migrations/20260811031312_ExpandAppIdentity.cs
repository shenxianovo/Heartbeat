using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class ExpandAppIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Apps_Name",
                table: "Apps");

            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "Apps",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsProvisional",
                table: "Apps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Key",
                table: "Apps",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "AppIdentityId",
                table: "ActivitySegments",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AppIdentities",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    AppId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppIdentities_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // 先复制产品呈现字段与短 Key，再删除旧 Name。只有真实短键碰撞时才增加限定；
            // 旧数据默认是已知 Windows 产品，不标 provisional。
            migrationBuilder.Sql(
                """
                WITH normalized AS (
                    SELECT
                        "Id",
                        left("Name", 256) AS display_name,
                        COALESCE(
                            NULLIF(trim(BOTH '-' FROM regexp_replace(
                                lower(regexp_replace(trim("Name"), '\.exe$', '', 'i')),
                                '[^a-z0-9.]+', '-', 'g')),
                            ''),
                            'app') AS base_key
                    FROM "Apps"
                ), ranked AS (
                    SELECT *, count(*) OVER (PARTITION BY base_key) AS collision_count
                    FROM normalized
                )
                UPDATE "Apps" AS app
                SET
                    "DisplayName" = ranked.display_name,
                    "Key" = CASE
                        WHEN ranked.collision_count = 1 THEN left(ranked.base_key, 256)
                        ELSE left('win.' || ranked.base_key || '.' || app."Id", 256)
                    END,
                    "IsProvisional" = false
                FROM ranked
                WHERE ranked."Id" = app."Id";

                WITH observed AS (
                    SELECT
                        "Id" AS app_id,
                        CASE
                            WHEN lower(trim("Name")) = '__away__' THEN 'sys:away'
                            ELSE 'win:' || lower(regexp_replace(trim("Name"), '\.exe$', '', 'i'))
                        END AS identity_key
                    FROM "Apps"
                )
                INSERT INTO "AppIdentities" ("Key", "AppId")
                SELECT identity_key, min(app_id)
                FROM observed
                GROUP BY identity_key;

                UPDATE "ActivitySegments" AS segment
                SET "AppIdentityId" = identity."Id"
                FROM "Apps" AS app
                JOIN "AppIdentities" AS identity
                  ON identity."Key" = CASE
                      WHEN lower(trim(app."Name")) = '__away__' THEN 'sys:away'
                      ELSE 'win:' || lower(regexp_replace(trim(app."Name"), '\.exe$', '', 'i'))
                  END
                WHERE segment."AppId" = app."Id";
                """);

            migrationBuilder.DropColumn(
                name: "Name",
                table: "Apps");

            migrationBuilder.CreateIndex(
                name: "IX_Apps_Key",
                table: "Apps",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivitySegments_AppIdentityId",
                table: "ActivitySegments",
                column: "AppIdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_AppIdentities_AppId",
                table: "AppIdentities",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_AppIdentities_Key",
                table: "AppIdentities",
                column: "Key",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ActivitySegments_AppIdentities_AppIdentityId",
                table: "ActivitySegments",
                column: "AppIdentityId",
                principalTable: "AppIdentities",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActivitySegments_AppIdentities_AppIdentityId",
                table: "ActivitySegments");

            migrationBuilder.DropTable(
                name: "AppIdentities");

            migrationBuilder.DropIndex(
                name: "IX_Apps_Key",
                table: "Apps");

            migrationBuilder.DropIndex(
                name: "IX_ActivitySegments_AppIdentityId",
                table: "ActivitySegments");

            migrationBuilder.DropColumn(
                name: "AppIdentityId",
                table: "ActivitySegments");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "Apps",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(
                """
                WITH ranked AS (
                    SELECT
                        "Id",
                        "DisplayName",
                        count(*) OVER (PARTITION BY "DisplayName") AS duplicate_count
                    FROM "Apps"
                )
                UPDATE "Apps" AS app
                SET "Name" = CASE
                    WHEN ranked.duplicate_count = 1 THEN ranked."DisplayName"
                    ELSE ranked."DisplayName" || '-' || app."Id"
                END
                FROM ranked
                WHERE ranked."Id" = app."Id";
                """);

            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "IsProvisional",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "Key",
                table: "Apps");

            migrationBuilder.CreateIndex(
                name: "IX_Apps_Name",
                table: "Apps",
                column: "Name",
                unique: true);
        }
    }
}
