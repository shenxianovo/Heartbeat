using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class VRChatServiceAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ServiceProducts",
                columns: table => new
                {
                    ServiceKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AppId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceProducts", x => x.ServiceKey);
                    table.ForeignKey(
                        name: "FK_ServiceProducts_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ServiceAccounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    ServiceKey = table.Column<string>(type: "character varying(64)", nullable: false),
                    ServiceAccountId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LegacySubjectId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceAccounts", x => x.Id);
                    table.CheckConstraint("CK_ServiceAccounts_Identity", "(\"ServiceAccountId\" IS NOT NULL AND \"LegacySubjectId\" IS NULL) OR (\"ServiceAccountId\" IS NULL AND \"LegacySubjectId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ServiceAccounts_ServiceProducts_ServiceKey",
                        column: x => x.ServiceKey,
                        principalTable: "ServiceProducts",
                        principalColumn: "ServiceKey",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ServiceAccounts_OwnerId_ServiceKey_LegacySubjectId",
                table: "ServiceAccounts",
                columns: new[] { "OwnerId", "ServiceKey", "LegacySubjectId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceAccounts_OwnerId_ServiceKey_ServiceAccountId",
                table: "ServiceAccounts",
                columns: new[] { "OwnerId", "ServiceKey", "ServiceAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ServiceAccounts_ServiceKey",
                table: "ServiceAccounts",
                column: "ServiceKey");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceProducts_AppId",
                table: "ServiceProducts",
                column: "AppId");
            migrationBuilder.Sql("""
                INSERT INTO "Apps" ("Key", "DisplayName", "IsProvisional") SELECT 'vrchat', 'VRChat', false
                  WHERE EXISTS (SELECT 1 FROM "Streams" WHERE "Source" = 'vrchat.account')
                  ON CONFLICT ("Key") DO NOTHING;
                INSERT INTO "ServiceProducts" ("ServiceKey", "AppId") SELECT 'vrchat', "Id" FROM "Apps" WHERE "Key" = 'vrchat';
                CREATE OR REPLACE FUNCTION heartbeat_check_fact_target() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."TargetKind" IS NULL AND NEW."TargetId" IS NULL THEN RETURN NEW; END IF;
                  IF NEW."TargetKind" = 'device' THEN
                    PERFORM 1 FROM "Devices" WHERE "Id" = NEW."TargetId" AND "OwnerId" = NEW."OwnerId" FOR SHARE;
                  ELSIF NEW."TargetKind" = 'application-context' THEN
                    PERFORM 1 FROM "ApplicationContexts" WHERE "Id" = NEW."TargetId" AND "OwnerId" = NEW."OwnerId" FOR SHARE;
                  ELSIF NEW."TargetKind" = 'account' THEN
                    PERFORM 1 FROM "ServiceAccounts" WHERE "Id" = NEW."TargetId" AND "OwnerId" = NEW."OwnerId" FOR SHARE;
                  ELSE RAISE foreign_key_violation USING MESSAGE = 'Unsupported or incomplete Fact Target';
                  END IF;
                  IF NOT FOUND THEN RAISE foreign_key_violation USING MESSAGE = 'Fact Target must exist within its Owner'; END IF;
                  RETURN NEW;
                END $$;
                CREATE OR REPLACE FUNCTION heartbeat_restrict_target_change() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE kind text;
                BEGIN
                  IF TG_OP = 'UPDATE' AND NEW."Id" = OLD."Id" AND NEW."OwnerId" = OLD."OwnerId" THEN RETURN NEW; END IF;
                  kind := CASE WHEN TG_TABLE_NAME = 'Devices' THEN 'device' WHEN TG_TABLE_NAME = 'ServiceAccounts' THEN 'account' ELSE 'application-context' END;
                  IF EXISTS (SELECT 1 FROM "Segments" WHERE "OwnerId" = OLD."OwnerId" AND "TargetKind" = kind AND "TargetId" = OLD."Id")
                    OR EXISTS (SELECT 1 FROM "Events" WHERE "OwnerId" = OLD."OwnerId" AND "TargetKind" = kind AND "TargetId" = OLD."Id")
                  THEN RAISE foreign_key_violation USING MESSAGE = 'Target is referenced by Facts'; END IF;
                  IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER "ServiceAccounts_Referenced" BEFORE DELETE OR UPDATE OF "Id", "OwnerId" ON "ServiceAccounts"
                  FOR EACH ROW EXECUTE FUNCTION heartbeat_restrict_target_change();
                CREATE FUNCTION heartbeat_account_identity_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF (NEW."OwnerId", NEW."ServiceKey", NEW."ServiceAccountId", NEW."LegacySubjectId") IS DISTINCT FROM
                     (OLD."OwnerId", OLD."ServiceKey", OLD."ServiceAccountId", OLD."LegacySubjectId")
                  THEN RAISE check_violation USING MESSAGE = 'Service account identity is immutable'; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER "ServiceAccounts_Identity" BEFORE UPDATE ON "ServiceAccounts"
                  FOR EACH ROW EXECUTE FUNCTION heartbeat_account_identity_immutable();
                """);
            // Deployed VRChat checkpoints/streams have no service account identifier. Preserve each
            // saved Subject as explicitly unknown; neither its UUID nor display name is a service ID.
            foreach (var table in new[] { "Segments", "Events" })
                migrationBuilder.Sql($$"""
                    INSERT INTO "ServiceAccounts" ("OwnerId", "ServiceKey", "LegacySubjectId")
                    SELECT DISTINCT f."OwnerId", 'vrchat', st."SubjectId" FROM "{{table}}" f
                    JOIN "Streams" st USING ("OwnerId", "StreamId")
                    JOIN "Subjects" sub ON sub."OwnerId" = st."OwnerId" AND sub."SubjectId" = st."SubjectId"
                    WHERE f."Source" = 'vrchat.account' AND sub."Kind" = 'account' AND f."TargetKind" IS NULL
                    ON CONFLICT ("OwnerId", "ServiceKey", "LegacySubjectId") DO NOTHING;
                    UPDATE "{{table}}" f SET "TargetKind" = 'account', "TargetId" = a."Id",
                      "ObserverId" = CASE WHEN st."Origin" = 'native' THEN st."CollectorInstanceId" ELSE NULL END
                    FROM "Streams" st, "ServiceAccounts" a
                    WHERE st."OwnerId" = f."OwnerId" AND st."StreamId" = f."StreamId"
                      AND a."OwnerId" = f."OwnerId" AND a."ServiceKey" = 'vrchat' AND a."LegacySubjectId" = st."SubjectId"
                      AND f."Source" = 'vrchat.account' AND f."TargetKind" IS NULL;
                    """);

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'Account Target rollback requires the pre-upgrade backup.'; END $$;");
        }
    }
}
