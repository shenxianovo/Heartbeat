using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class BrowserApplicationContexts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Devices_OwnerId_Id",
                table: "Devices",
                columns: new[] { "OwnerId", "Id" });

            migrationBuilder.CreateTable(
                name: "ApplicationContexts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    DeviceId = table.Column<long>(type: "bigint", nullable: false),
                    AppId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApplicationContexts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApplicationContexts_Apps_AppId",
                        column: x => x.AppId,
                        principalTable: "Apps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ApplicationContexts_Devices_OwnerId_DeviceId",
                        columns: x => new { x.OwnerId, x.DeviceId },
                        principalTable: "Devices",
                        principalColumns: new[] { "OwnerId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationContexts_AppId",
                table: "ApplicationContexts",
                column: "AppId");

            migrationBuilder.CreateIndex(
                name: "IX_ApplicationContexts_OwnerId_DeviceId_AppId",
                table: "ApplicationContexts",
                columns: new[] { "OwnerId", "DeviceId", "AppId" },
                unique: true);
            // Only saved device + platform-product evidence maps Browser history. No name guesses.
            foreach (var table in new[] { "Segments", "Events" })
                migrationBuilder.Sql($$"""
                    INSERT INTO "ApplicationContexts" ("OwnerId", "DeviceId", "AppId")
                    SELECT DISTINCT f."OwnerId", subject."DeviceId", app."AppId"
                    FROM "{{table}}" f JOIN "Streams" stream USING ("OwnerId", "StreamId")
                    JOIN "Subjects" subject ON subject."OwnerId" = stream."OwnerId" AND subject."SubjectId" = stream."SubjectId"
                    JOIN "AppIdentities" app ON app."Id" = f."AppIdentityId"
                    WHERE f."Source" = 'browser' AND subject."Kind" = 'machine' AND subject."DeviceId" IS NOT NULL
                      AND f."TargetKind" IS NULL
                    ON CONFLICT ("OwnerId", "DeviceId", "AppId") DO NOTHING;
                    UPDATE "{{table}}" f SET
                      "TargetKind" = CASE WHEN context."Id" IS NULL THEN 'device' ELSE 'application-context' END,
                      "TargetId" = COALESCE(context."Id", subject."DeviceId"),
                      "ObserverId" = CASE WHEN stream."Origin" = 'native'
                        AND stream."Dimensions"->>'externalHostIdentity' ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
                        THEN NULLIF((stream."Dimensions"->>'externalHostIdentity')::uuid, '00000000-0000-0000-0000-000000000000'::uuid) ELSE NULL END
                    FROM "Streams" stream JOIN "Subjects" subject
                      ON subject."OwnerId" = stream."OwnerId" AND subject."SubjectId" = stream."SubjectId",
                      "{{table}}" original LEFT JOIN "AppIdentities" app ON app."Id" = original."AppIdentityId"
                      LEFT JOIN "Streams" original_stream ON original_stream."OwnerId" = original."OwnerId" AND original_stream."StreamId" = original."StreamId"
                      LEFT JOIN "Subjects" original_subject ON original_subject."OwnerId" = original_stream."OwnerId" AND original_subject."SubjectId" = original_stream."SubjectId"
                      LEFT JOIN "ApplicationContexts" context ON context."OwnerId" = original."OwnerId" AND context."DeviceId" = original_subject."DeviceId" AND context."AppId" = app."AppId"
                    WHERE f."Id" = original."Id" AND f."OwnerId" = stream."OwnerId" AND f."StreamId" = stream."StreamId"
                      AND f."Source" = 'browser' AND f."TargetKind" IS NULL
                      AND subject."Kind" = 'machine' AND subject."DeviceId" IS NOT NULL;
                    """);

            // A polymorphic Target keeps one pair of columns. Guard the supported concrete references
            // in PostgreSQL too, including direct SQL and concurrent deletion, without an Objects table.
            migrationBuilder.Sql("""
                CREATE FUNCTION heartbeat_check_fact_target() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."TargetKind" IS NULL AND NEW."TargetId" IS NULL THEN RETURN NEW; END IF;
                  IF NEW."TargetKind" = 'device' THEN
                    PERFORM 1 FROM "Devices" WHERE "Id" = NEW."TargetId" AND "OwnerId" = NEW."OwnerId" FOR SHARE;
                  ELSIF NEW."TargetKind" = 'application-context' THEN
                    PERFORM 1 FROM "ApplicationContexts" WHERE "Id" = NEW."TargetId" AND "OwnerId" = NEW."OwnerId" FOR SHARE;
                  ELSE RAISE foreign_key_violation USING MESSAGE = 'Unsupported or incomplete Fact Target';
                  END IF;
                  IF NOT FOUND THEN RAISE foreign_key_violation USING MESSAGE = 'Fact Target must exist within its Owner'; END IF;
                  RETURN NEW;
                END $$;
                CREATE FUNCTION heartbeat_restrict_target_change() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE kind text;
                BEGIN
                  IF TG_OP = 'UPDATE' AND NEW."Id" = OLD."Id" AND NEW."OwnerId" = OLD."OwnerId" THEN RETURN NEW; END IF;
                  kind := CASE WHEN TG_TABLE_NAME = 'Devices' THEN 'device' ELSE 'application-context' END;
                  IF EXISTS (SELECT 1 FROM "Segments" WHERE "OwnerId" = OLD."OwnerId" AND "TargetKind" = kind AND "TargetId" = OLD."Id")
                    OR EXISTS (SELECT 1 FROM "Events" WHERE "OwnerId" = OLD."OwnerId" AND "TargetKind" = kind AND "TargetId" = OLD."Id")
                  THEN RAISE foreign_key_violation USING MESSAGE = 'Target is referenced by Facts'; END IF;
                  IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER "Segments_Target" BEFORE INSERT OR UPDATE OF "OwnerId", "TargetKind", "TargetId" ON "Segments"
                  FOR EACH ROW EXECUTE FUNCTION heartbeat_check_fact_target();
                CREATE TRIGGER "Events_Target" BEFORE INSERT OR UPDATE OF "OwnerId", "TargetKind", "TargetId" ON "Events"
                  FOR EACH ROW EXECUTE FUNCTION heartbeat_check_fact_target();
                CREATE TRIGGER "ApplicationContexts_Referenced" BEFORE DELETE OR UPDATE OF "Id", "OwnerId" ON "ApplicationContexts"
                  FOR EACH ROW EXECUTE FUNCTION heartbeat_restrict_target_change();
                CREATE TRIGGER "Devices_TargetReferenced" BEFORE DELETE OR UPDATE OF "Id", "OwnerId" ON "Devices"
                  FOR EACH ROW EXECUTE FUNCTION heartbeat_restrict_target_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DO $$ BEGIN RAISE EXCEPTION 'Browser Target rollback requires the pre-upgrade backup.'; END $$;");
        }
    }
}
