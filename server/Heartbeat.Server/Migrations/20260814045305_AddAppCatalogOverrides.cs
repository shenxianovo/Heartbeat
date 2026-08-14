using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddAppCatalogOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppCatalogOverrides",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppIdentityId = table.Column<long>(type: "bigint", nullable: false),
                    TargetAppId = table.Column<long>(type: "bigint", nullable: true),
                    TargetAppKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedBySubject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UpdatedBySubject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PromotedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppCatalogOverrides", x => x.Id);
                    table.CheckConstraint("CK_AppCatalogOverrides_ActiveTarget", "\"Status\" <> 'active' OR \"TargetAppId\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_AppCatalogOverrides_AppIdentities_AppIdentityId",
                        column: x => x.AppIdentityId,
                        principalTable: "AppIdentities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AppCatalogOverrides_Apps_TargetAppId",
                        column: x => x.TargetAppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppCatalogOverrides_AppIdentityId",
                table: "AppCatalogOverrides",
                column: "AppIdentityId",
                unique: true,
                filter: "\"Status\" = 'active'");

            migrationBuilder.CreateIndex(
                name: "IX_AppCatalogOverrides_TargetAppId",
                table: "AppCatalogOverrides",
                column: "TargetAppId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppCatalogOverrides");
        }
    }
}
