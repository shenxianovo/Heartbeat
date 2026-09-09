using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Server.Migrations;

/// <summary>Unreleased replacement: evolve old rows directly into family storage (ADR-055).</summary>
public partial class NativeFactCustody : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Subjects",
            columns: table => new
            {
                OwnerId = table.Column<string>(type: "text", nullable: false),
                SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                Kind = table.Column<string>(type: "text", nullable: false),
                DeviceId = table.Column<long>(type: "bigint", nullable: true),
                DisplayName = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Subjects", x => new { x.OwnerId, x.SubjectId });
                table.ForeignKey(
                    name: "FK_Subjects_Devices_DeviceId",
                    column: x => x.DeviceId,
                    principalTable: "Devices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Streams",
            columns: table => new
            {
                OwnerId = table.Column<string>(type: "text", nullable: false),
                StreamId = table.Column<Guid>(type: "uuid", nullable: false),
                SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                CollectorInstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                OutputId = table.Column<string>(type: "text", nullable: false),
                Source = table.Column<string>(type: "text", nullable: false),
                FactKind = table.Column<string>(type: "text", nullable: false),
                Dimensions = table.Column<string>(type: "jsonb", nullable: false),
                Origin = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Streams", x => new { x.OwnerId, x.StreamId });
                table.ForeignKey(
                    name: "FK_Streams_Subjects_OwnerId_SubjectId",
                    columns: x => new { x.OwnerId, x.SubjectId },
                    principalTable: "Subjects",
                    principalColumns: new[] { "OwnerId", "SubjectId" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "FactGaps",
            columns: table => new
            {
                OwnerId = table.Column<string>(type: "text", nullable: false),
                StreamId = table.Column<Guid>(type: "uuid", nullable: false),
                GapId = table.Column<Guid>(type: "uuid", nullable: false),
                Start = table.Column<long>(type: "bigint", nullable: false),
                End = table.Column<long>(type: "bigint", nullable: false),
                Reason = table.Column<string>(type: "text", nullable: false),
                EstimatedFactsLost = table.Column<int>(type: "integer", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FactGaps", x => new { x.OwnerId, x.StreamId, x.GapId });
                table.ForeignKey(
                    name: "FK_FactGaps_Streams_OwnerId_StreamId",
                    columns: x => new { x.OwnerId, x.StreamId },
                    principalTable: "Streams",
                    principalColumns: new[] { "OwnerId", "StreamId" },
                    onDelete: ReferentialAction.Restrict);
            });

        // Separate commands expose phase timings while retaining EF's migration transaction.
        migrationBuilder.Sql("""
                DO $migration$
                BEGIN
                  IF EXISTS (SELECT 1 FROM "ActivitySegments" WHERE "AppId" IS NOT NULL AND "AppIdentityId" IS NULL) THEN
                    RAISE EXCEPTION 'ActivitySegment has an App without AppIdentity; resolve the mapping before migration';
                  END IF;
                  IF EXISTS (SELECT 1 FROM "InputEvents" WHERE "EventType" NOT IN (1, 2, 3)) THEN
                    RAISE EXCEPTION 'InputEvent has an unknown EventType';
                  END IF;
                  IF EXISTS (SELECT 1 FROM "ActivitySegments" s
                    WHERE jsonb_typeof(s."Attributes") = 'object'
                      AND jsonb_typeof(s."Attributes"->'identityKey') = 'string'
                      AND s."Attributes"->>'identityKey' = s."IdentityKey"
                      AND (jsonb_typeof(s."Attributes"->'title') IS NULL OR jsonb_typeof(s."Attributes"->'title') IN ('string', 'null'))
                      AND (s."Attributes"->>'title') IS NOT DISTINCT FROM s."Title"
                      AND jsonb_typeof(s."Attributes"->'attributes') = 'object'
                      AND s."Attributes" ? 'activityKey'
                      AND s."Attributes"->'activityKey' IS DISTINCT FROM to_jsonb(s."IdentityKey")) THEN
                    RAISE EXCEPTION 'Historical activityKey conflicts with identityKey';
                  END IF;
                END $migration$;
                """);

        migrationBuilder.Sql("""
                ALTER TABLE "ActivitySegments" RENAME TO "Segments";
                ALTER TABLE "InputEvents" RENAME TO "Events";
                ALTER TABLE "Segments" RENAME CONSTRAINT "PK_ActivitySegments" TO "PK_Segments";
                ALTER TABLE "Events" RENAME CONSTRAINT "PK_InputEvents" TO "PK_Events";
                ALTER TABLE "Segments" RENAME CONSTRAINT "FK_ActivitySegments_AppIdentities_AppIdentityId" TO "FK_Segments_AppIdentities_AppIdentityId";
                ALTER INDEX "IX_ActivitySegments_AppIdentityId" RENAME TO "IX_Segments_AppIdentityId";
                DROP INDEX "IX_ActivitySegments_StartTime";
                ALTER TABLE "Segments"
                  ADD COLUMN "OwnerId" text,
                  ADD COLUMN "StreamId" uuid,
                  ADD COLUMN "FactId" uuid,
                  ADD COLUMN "Revision" bigint NOT NULL DEFAULT 1,
                  ADD COLUMN "Payload" jsonb;
                ALTER TABLE "Events"
                  ADD COLUMN "OwnerId" text,
                  ADD COLUMN "StreamId" uuid,
                  ADD COLUMN "FactId" uuid,
                  ADD COLUMN "Revision" bigint NOT NULL DEFAULT 1,
                  ADD COLUMN "Source" varchar(64) NOT NULL DEFAULT 'system',
                  ADD COLUMN "AppIdentityId" bigint,
                  ADD COLUMN "Payload" jsonb;
                """);

        migrationBuilder.Sql("""
                INSERT INTO "Subjects" ("OwnerId", "SubjectId", "Kind", "DeviceId", "DisplayName")
                SELECT DISTINCT d."OwnerId",
                  CASE WHEN d."HardwareId" ~ '^subject:(account|person):[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' THEN split_part(d."HardwareId", ':', 3)::uuid
                       ELSE md5('legacy-subject:' || d."OwnerId" || ':' || d."Id")::uuid END,
                  CASE WHEN d."HardwareId" ~ '^subject:(account|person):[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' THEN split_part(d."HardwareId", ':', 2) ELSE 'machine' END,
                  CASE WHEN d."HardwareId" ~ '^subject:(account|person):[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' THEN NULL ELSE d."Id" END,
                  d."DeviceName"
                FROM "Devices" d
                ON CONFLICT DO NOTHING;

                INSERT INTO "Streams" ("OwnerId", "StreamId", "SubjectId", "CollectorInstanceId", "OutputId", "Source", "FactKind", "Dimensions", "Origin")
                SELECT DISTINCT d."OwnerId", md5('legacy-stream:' || d."OwnerId" || ':' || d."Id" || ':' || s."Source" || ':segment')::uuid,
                  CASE WHEN d."HardwareId" ~ '^subject:(account|person):[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' THEN split_part(d."HardwareId", ':', 3)::uuid
                       ELSE md5('legacy-subject:' || d."OwnerId" || ':' || d."Id")::uuid END,
                  NULL::uuid, 'legacy-import', s."Source", 'segment', '{}'::jsonb, 'legacy-import'
                FROM (SELECT DISTINCT "DeviceId", "Source" FROM "Segments") s JOIN "Devices" d ON d."Id" = s."DeviceId";

                INSERT INTO "Streams" ("OwnerId", "StreamId", "SubjectId", "CollectorInstanceId", "OutputId", "Source", "FactKind", "Dimensions", "Origin")
                SELECT DISTINCT d."OwnerId", md5('legacy-stream:' || d."OwnerId" || ':' || d."Id" || ':system:event')::uuid,
                  CASE WHEN d."HardwareId" ~ '^subject:(account|person):[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' THEN split_part(d."HardwareId", ':', 3)::uuid
                       ELSE md5('legacy-subject:' || d."OwnerId" || ':' || d."Id")::uuid END,
                  NULL::uuid, 'legacy-import', 'system', 'event', '{}'::jsonb, 'legacy-import'
                FROM (SELECT DISTINCT "DeviceId" FROM "Events") e JOIN "Devices" d ON d."Id" = e."DeviceId";
                """);

        migrationBuilder.Sql("""
                UPDATE "Segments" s SET
                  "OwnerId" = d."OwnerId",
                  "StreamId" = md5('legacy-stream:' || d."OwnerId" || ':' || d."Id" || ':' || s."Source" || ':segment')::uuid,
                  "FactId" = s."Id",
                  "Payload" = CASE WHEN jsonb_typeof(s."Attributes") = 'object'
                             AND jsonb_typeof(s."Attributes"->'identityKey') = 'string'
                      AND s."Attributes"->>'identityKey' = s."IdentityKey"
                             AND (jsonb_typeof(s."Attributes"->'title') IS NULL OR jsonb_typeof(s."Attributes"->'title') IN ('string', 'null'))
                      AND (s."Attributes"->>'title') IS NOT DISTINCT FROM s."Title"
                             AND jsonb_typeof(s."Attributes"->'attributes') = 'object'
                       THEN (s."Attributes" - 'identityKey') || jsonb_build_object('activityKey', s."IdentityKey")
                       ELSE jsonb_build_object('activityKey', s."IdentityKey", 'title', s."Title", 'attributes', s."Attributes") END
                FROM "Devices" d WHERE d."Id" = s."DeviceId";
                """);

        migrationBuilder.Sql("""
                UPDATE "Events" e SET
                  "OwnerId" = d."OwnerId",
                  "StreamId" = md5('legacy-stream:' || d."OwnerId" || ':' || d."Id" || ':system:event')::uuid,
                  "FactId" = e."Id",
                  "Payload" = jsonb_build_object('eventType', CASE e."EventType" WHEN 1 THEN 'keyDown' WHEN 2 THEN 'mouseButton' WHEN 3 THEN 'mouseScroll' END,
                    'codeSet', e."CodeSet", 'code', e."Code")
                FROM "Devices" d WHERE d."Id" = e."DeviceId";
                """);

        migrationBuilder.Sql("""
                -- Owner/Stream FKs and NOT NULL validate that every old row acquired an identity.
                -- Dropped columns also remove their obsolete indexes/FKs; no full-row archive is retained.
                ALTER TABLE "Segments"
                  ALTER COLUMN "OwnerId" SET NOT NULL,
                  ALTER COLUMN "StreamId" SET NOT NULL,
                  ALTER COLUMN "FactId" SET NOT NULL,
                  ALTER COLUMN "Payload" SET NOT NULL,
                  ALTER COLUMN "Revision" DROP DEFAULT,
                  DROP COLUMN "DeviceId", DROP COLUMN "AppId", DROP COLUMN "IdentityKey",
                  DROP COLUMN "Title", DROP COLUMN "Attributes",
                  ADD CONSTRAINT "CK_Segments_Revision" CHECK ("Revision" >= 1),
                  ADD CONSTRAINT "FK_Segments_Streams_OwnerId_StreamId" FOREIGN KEY ("OwnerId", "StreamId") REFERENCES "Streams" ("OwnerId", "StreamId") ON DELETE RESTRICT;
                ALTER TABLE "Events"
                  ALTER COLUMN "OwnerId" SET NOT NULL,
                  ALTER COLUMN "StreamId" SET NOT NULL,
                  ALTER COLUMN "FactId" SET NOT NULL,
                  ALTER COLUMN "Payload" SET NOT NULL,
                  ALTER COLUMN "Revision" DROP DEFAULT,
                  ALTER COLUMN "Source" DROP DEFAULT,
                  DROP COLUMN "DeviceId", DROP COLUMN "EventType", DROP COLUMN "CodeSet", DROP COLUMN "Code",
                  ADD CONSTRAINT "CK_Events_Revision" CHECK ("Revision" >= 1),
                  ADD CONSTRAINT "FK_Events_AppIdentities_AppIdentityId" FOREIGN KEY ("AppIdentityId") REFERENCES "AppIdentities" ("Id") ON DELETE RESTRICT,
                  ADD CONSTRAINT "FK_Events_Streams_OwnerId_StreamId" FOREIGN KEY ("OwnerId", "StreamId") REFERENCES "Streams" ("OwnerId", "StreamId") ON DELETE RESTRICT;
                """);

        // Parallel index construction OOMs on the full historical Events table at
        // 512 MiB / 0.75 CPU. Serial construction fits; keep this migration-local.
        migrationBuilder.Sql("SET LOCAL max_parallel_maintenance_workers = 0;");

        migrationBuilder.CreateIndex(
            name: "IX_Events_AppIdentityId",
            table: "Events",
            column: "AppIdentityId");

        migrationBuilder.CreateIndex(
            name: "IX_Events_OwnerId_FactId",
            table: "Events",
            columns: new[] { "OwnerId", "FactId" });

        migrationBuilder.CreateIndex(
            name: "IX_Events_OwnerId_StreamId_FactId",
            table: "Events",
            columns: new[] { "OwnerId", "StreamId", "FactId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Events_OwnerId_Timestamp",
            table: "Events",
            columns: new[] { "OwnerId", "Timestamp" });

        migrationBuilder.CreateIndex(
            name: "IX_Segments_OwnerId_FactId",
            table: "Segments",
            columns: new[] { "OwnerId", "FactId" });

        migrationBuilder.CreateIndex(
            name: "IX_Segments_OwnerId_Source_StartTime",
            table: "Segments",
            columns: new[] { "OwnerId", "Source", "StartTime" });

        migrationBuilder.CreateIndex(
            name: "IX_Segments_OwnerId_StreamId_FactId",
            table: "Segments",
            columns: new[] { "OwnerId", "StreamId", "FactId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Streams_OwnerId_SubjectId",
            table: "Streams",
            columns: new[] { "OwnerId", "SubjectId" });

        migrationBuilder.CreateIndex(
            name: "IX_Subjects_DeviceId",
            table: "Subjects",
            column: "DeviceId");
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $migration$ BEGIN
          RAISE EXCEPTION 'Fact family migration cannot reconstruct discarded physical columns; restore the pre-upgrade backup and retain new facts';
        END $migration$;
        """);
}
