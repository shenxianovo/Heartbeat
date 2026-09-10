using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class PersonAssociations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_ServiceAccounts_OwnerId_Id",
                table: "ServiceAccounts",
                columns: new[] { "OwnerId", "Id" });

            migrationBuilder.CreateTable(
                name: "Persons",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    Reference = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Persons", x => x.Id);
                    table.UniqueConstraint("AK_Persons_OwnerId_Id", x => new { x.OwnerId, x.Id });
                    table.ForeignKey(
                        name: "FK_Persons_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PersonAssociations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    PersonId = table.Column<long>(type: "bigint", nullable: false),
                    DeviceId = table.Column<long>(type: "bigint", nullable: true),
                    AccountId = table.Column<long>(type: "bigint", nullable: true),
                    Start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    End = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonAssociations", x => x.Id);
                    table.CheckConstraint("CK_PersonAssociations_Interval", "(\"Start\" IS NULL OR isfinite(\"Start\")) AND (\"End\" IS NULL OR isfinite(\"End\")) AND (\"Start\" IS NULL OR \"End\" IS NULL OR \"Start\" < \"End\")");
                    table.CheckConstraint("CK_PersonAssociations_Target", "(\"DeviceId\" IS NULL) <> (\"AccountId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_PersonAssociations_Devices_OwnerId_DeviceId",
                        columns: x => new { x.OwnerId, x.DeviceId },
                        principalTable: "Devices",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PersonAssociations_Persons_OwnerId_PersonId",
                        columns: x => new { x.OwnerId, x.PersonId },
                        principalTable: "Persons",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PersonAssociations_ServiceAccounts_OwnerId_AccountId",
                        columns: x => new { x.OwnerId, x.AccountId },
                        principalTable: "ServiceAccounts",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PersonAssociations_OwnerId_AccountId_Start_End",
                table: "PersonAssociations",
                columns: new[] { "OwnerId", "AccountId", "Start", "End" });

            migrationBuilder.CreateIndex(
                name: "IX_PersonAssociations_OwnerId_DeviceId_Start_End",
                table: "PersonAssociations",
                columns: new[] { "OwnerId", "DeviceId", "Start", "End" });

            migrationBuilder.CreateIndex(
                name: "IX_PersonAssociations_OwnerId_PersonId",
                table: "PersonAssociations",
                columns: new[] { "OwnerId", "PersonId" });

            migrationBuilder.CreateIndex(
                name: "IX_Persons_OwnerId",
                table: "Persons",
                column: "OwnerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Persons_Reference",
                table: "Persons",
                column: "Reference",
                unique: true);
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION heartbeat_check_fact_target() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."TargetKind" IS NULL AND NEW."TargetId" IS NULL THEN RETURN NEW; END IF;
                  IF NEW."TargetKind" = 'device' THEN
                    PERFORM 1 FROM "Devices" WHERE "Id" = NEW."TargetId" AND "OwnerId" = NEW."OwnerId" FOR SHARE;
                  ELSIF NEW."TargetKind" = 'application-context' THEN
                    PERFORM 1 FROM "ApplicationContexts" WHERE "Id" = NEW."TargetId" AND "OwnerId" = NEW."OwnerId" FOR SHARE;
                  ELSIF NEW."TargetKind" = 'account' THEN
                    PERFORM 1 FROM "ServiceAccounts" WHERE "Id" = NEW."TargetId" AND "OwnerId" = NEW."OwnerId" FOR SHARE;
                  ELSIF NEW."TargetKind" = 'person' THEN
                    -- A tuple version is necessary, not just a lock: an older Repeatable Read
                    -- deleter must abort even when its snapshot cannot see this new Fact.
                    UPDATE "Persons" SET "Reference" = "Reference"
                      WHERE "Id" = NEW."TargetId" AND "OwnerId" = NEW."OwnerId";
                  ELSE RAISE foreign_key_violation USING MESSAGE = 'Unsupported or incomplete Fact Target';
                  END IF;
                  IF NOT FOUND THEN RAISE foreign_key_violation USING MESSAGE = 'Fact Target must exist within its Owner'; END IF;
                  RETURN NEW;
                END $$;
                CREATE OR REPLACE FUNCTION heartbeat_restrict_target_change() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE kind text;
                BEGIN
                  IF TG_OP = 'UPDATE' AND NEW."Id" = OLD."Id" AND NEW."OwnerId" = OLD."OwnerId" THEN RETURN NEW; END IF;
                  kind := CASE WHEN TG_TABLE_NAME = 'Devices' THEN 'device' WHEN TG_TABLE_NAME = 'ServiceAccounts' THEN 'account'
                    WHEN TG_TABLE_NAME = 'Persons' THEN 'person' ELSE 'application-context' END;
                  IF EXISTS (SELECT 1 FROM "Segments" WHERE "OwnerId" = OLD."OwnerId" AND "TargetKind" = kind AND "TargetId" = OLD."Id")
                    OR EXISTS (SELECT 1 FROM "Events" WHERE "OwnerId" = OLD."OwnerId" AND "TargetKind" = kind AND "TargetId" = OLD."Id")
                  THEN RAISE foreign_key_violation USING MESSAGE = 'Target is referenced by Facts'; END IF;
                  IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER "Persons_Referenced" BEFORE DELETE OR UPDATE OF "Id", "OwnerId" ON "Persons"
                  FOR EACH ROW EXECUTE FUNCTION heartbeat_restrict_target_change();
                CREATE FUNCTION heartbeat_person_identity_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF (NEW."Id", NEW."OwnerId", NEW."Reference") IS DISTINCT FROM (OLD."Id", OLD."OwnerId", OLD."Reference")
                  THEN RAISE check_violation USING MESSAGE = 'Person identity is immutable'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER "Persons_Identity" BEFORE UPDATE ON "Persons"
                  FOR EACH ROW EXECUTE FUNCTION heartbeat_person_identity_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'Person Target rollback requires the pre-upgrade backup.'; END $$;");
        }
    }
}
