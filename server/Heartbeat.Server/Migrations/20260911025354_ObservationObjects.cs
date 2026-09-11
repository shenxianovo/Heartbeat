using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class ObservationObjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ObjectId",
                table: "ServiceAccounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ObjectId",
                table: "Persons",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Aspect",
                table: "Facts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FoiId",
                table: "Facts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ObjectId",
                table: "Devices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ObjectId",
                table: "Apps",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_PersonAssociations_OwnerId_Id",
                table: "PersonAssociations",
                columns: new[] { "OwnerId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Facts_OwnerId_Id",
                table: "Facts",
                columns: new[] { "OwnerId", "Id" });

            migrationBuilder.CreateTable(
                name: "Collectors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Collectors", x => new { x.OwnerId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "Objects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    OwnerId = table.Column<string>(type: "text", nullable: true),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Scope = table.Column<string>(type: "text", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Objects", x => x.Id);
                    table.CheckConstraint("CK_Objects_KindOwner", "(\"Kind\" = 'app' AND \"OwnerId\" IS NULL) OR (\"Kind\" IN ('machine','account','person') AND \"OwnerId\" IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "Relations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ValidTo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Evidence = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    FactId = table.Column<Guid>(type: "uuid", nullable: true, computedColumnSql: "(\"Evidence\"->>'factId')::uuid", stored: true),
                    AssociationId = table.Column<long>(type: "bigint", nullable: true, computedColumnSql: "(\"Evidence\"->>'associationId')::bigint", stored: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Relations", x => x.Id);
                    table.CheckConstraint("CK_Relations_Time", "(\"ValidFrom\" IS NULL OR isfinite(\"ValidFrom\")) AND (\"ValidTo\" IS NULL OR isfinite(\"ValidTo\")) AND (\"ValidFrom\" IS NULL OR \"ValidTo\" IS NULL OR \"ValidFrom\" <= \"ValidTo\")");
                    table.ForeignKey(
                        name: "FK_Relations_Facts_OwnerId_FactId",
                        columns: x => new { x.OwnerId, x.FactId },
                        principalTable: "Facts",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Relations_PersonAssociations_OwnerId_AssociationId",
                        columns: x => new { x.OwnerId, x.AssociationId },
                        principalTable: "PersonAssociations",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RelationMembers",
                columns: table => new
                {
                    RelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    ObjectId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelationMembers", x => new { x.RelationId, x.Role, x.ObjectId });
                    table.ForeignKey(
                        name: "FK_RelationMembers_Objects_ObjectId",
                        column: x => x.ObjectId,
                        principalTable: "Objects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RelationMembers_Relations_RelationId",
                        column: x => x.RelationId,
                        principalTable: "Relations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceAccounts_ObjectId",
                table: "ServiceAccounts",
                column: "ObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Persons_ObjectId",
                table: "Persons",
                column: "ObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Facts_FoiId",
                table: "Facts",
                column: "FoiId");

            migrationBuilder.CreateIndex(
                name: "IX_Facts_OwnerId_CollectorId",
                table: "Facts",
                columns: new[] { "OwnerId", "CollectorId" });

            migrationBuilder.CreateIndex(
                name: "IX_Facts_OwnerId_FoiId",
                table: "Facts",
                columns: new[] { "OwnerId", "FoiId" });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_ObjectId",
                table: "Devices",
                column: "ObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Apps_ObjectId",
                table: "Apps",
                column: "ObjectId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Objects_Kind_Scope_Key",
                table: "Objects",
                columns: new[] { "Kind", "Scope", "Key" },
                unique: true,
                filter: "\"OwnerId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Objects_OwnerId_Kind_Scope_Key",
                table: "Objects",
                columns: new[] { "OwnerId", "Kind", "Scope", "Key" },
                unique: true,
                filter: "\"OwnerId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RelationMembers_ObjectId_Role",
                table: "RelationMembers",
                columns: new[] { "ObjectId", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_Relations_OwnerId_AssociationId",
                table: "Relations",
                columns: new[] { "OwnerId", "AssociationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Relations_OwnerId_FactId",
                table: "Relations",
                columns: new[] { "OwnerId", "FactId" });

            migrationBuilder.CreateIndex(
                name: "IX_Relations_OwnerId_Kind_AssociationId",
                table: "Relations",
                columns: new[] { "OwnerId", "Kind", "AssociationId" },
                unique: true,
                filter: "\"AssociationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Relations_OwnerId_Kind_FactId",
                table: "Relations",
                columns: new[] { "OwnerId", "Kind", "FactId" },
                unique: true,
                filter: "\"FactId\" IS NOT NULL");

            migrationBuilder.Sql(StorageSql);

            migrationBuilder.AddForeignKey(
                name: "FK_Apps_Objects_ObjectId",
                table: "Apps",
                column: "ObjectId",
                principalTable: "Objects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Devices_Objects_ObjectId",
                table: "Devices",
                column: "ObjectId",
                principalTable: "Objects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Facts_Collectors_OwnerId_CollectorId",
                table: "Facts",
                columns: new[] { "OwnerId", "CollectorId" },
                principalTable: "Collectors",
                principalColumns: new[] { "OwnerId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Facts_Objects_FoiId",
                table: "Facts",
                column: "FoiId",
                principalTable: "Objects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Persons_Objects_ObjectId",
                table: "Persons",
                column: "ObjectId",
                principalTable: "Objects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ServiceAccounts_Objects_ObjectId",
                table: "ServiceAccounts",
                column: "ObjectId",
                principalTable: "Objects",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
            DO $$ BEGIN RAISE EXCEPTION 'Observation storage rollback requires the pre-upgrade backup and custody of subsequently accepted facts.'; END $$;
            """);
    }
}
