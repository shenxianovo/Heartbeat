using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class NativeFactCustody : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActivitySegments_Devices_DeviceId",
                table: "ActivitySegments");

            migrationBuilder.AddColumn<Guid>(
                name: "FactKey",
                table: "InputEvents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "DeviceId",
                table: "ActivitySegments",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<Guid>(
                name: "FactKey",
                table: "ActivitySegments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnerId",
                table: "ActivitySegments",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Payload",
                table: "ActivitySegments",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FactSchemas",
                columns: table => new
                {
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    SchemaId = table.Column<string>(type: "text", nullable: false),
                    SchemaMajor = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    DocumentJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FactSchemas", x => new { x.OwnerId, x.SchemaId, x.SchemaMajor, x.Revision });
                });

            migrationBuilder.CreateTable(
                name: "FactSubjects",
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
                    table.PrimaryKey("PK_FactSubjects", x => new { x.OwnerId, x.SubjectId });
                    table.ForeignKey(
                        name: "FK_FactSubjects_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FactStreams",
                columns: table => new
                {
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    StreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectorInstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    OutputId = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "text", nullable: false),
                    FactKind = table.Column<string>(type: "text", nullable: false),
                    SchemaId = table.Column<string>(type: "text", nullable: false),
                    SchemaMajor = table.Column<int>(type: "integer", nullable: false),
                    Dimensions = table.Column<string>(type: "jsonb", nullable: false),
                    Origin = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FactStreams", x => new { x.OwnerId, x.StreamId });
                    table.ForeignKey(
                        name: "FK_FactStreams_FactSubjects_OwnerId_SubjectId",
                        columns: x => new { x.OwnerId, x.SubjectId },
                        principalTable: "FactSubjects",
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
                        name: "FK_FactGaps_FactStreams_OwnerId_StreamId",
                        columns: x => new { x.OwnerId, x.StreamId },
                        principalTable: "FactStreams",
                        principalColumns: new[] { "OwnerId", "StreamId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Facts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    StreamId = table.Column<Guid>(type: "uuid", nullable: false),
                    FactId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    SchemaRevision = table.Column<int>(type: "integer", nullable: false),
                    Origin = table.Column<string>(type: "text", nullable: false),
                    ObservedAt = table.Column<long>(type: "bigint", nullable: true),
                    Start = table.Column<long>(type: "bigint", nullable: true),
                    End = table.Column<long>(type: "bigint", nullable: true),
                    OccurredAt = table.Column<long>(type: "bigint", nullable: true),
                    IsFinal = table.Column<bool>(type: "boolean", nullable: true),
                    Payload = table.Column<string>(type: "jsonb", nullable: true),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    LegacyRecord = table.Column<string>(type: "jsonb", nullable: true),
                    LegacyId = table.Column<Guid>(type: "uuid", nullable: true),
                    LegacyDeviceId = table.Column<long>(type: "bigint", nullable: true),
                    LegacyKind = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Facts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Facts_FactStreams_OwnerId_StreamId",
                        columns: x => new { x.OwnerId, x.StreamId },
                        principalTable: "FactStreams",
                        principalColumns: new[] { "OwnerId", "StreamId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InputEvents_FactKey",
                table: "InputEvents",
                column: "FactKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivitySegments_FactKey",
                table: "ActivitySegments",
                column: "FactKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Facts_OwnerId_LegacyDeviceId_LegacyKind_LegacyId",
                table: "Facts",
                columns: new[] { "OwnerId", "LegacyDeviceId", "LegacyKind", "LegacyId" });

            migrationBuilder.CreateIndex(
                name: "IX_Facts_OwnerId_StreamId_FactId",
                table: "Facts",
                columns: new[] { "OwnerId", "StreamId", "FactId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FactStreams_OwnerId_SubjectId",
                table: "FactStreams",
                columns: new[] { "OwnerId", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_FactSubjects_DeviceId",
                table: "FactSubjects",
                column: "DeviceId");

            migrationBuilder.AddForeignKey(
                name: "FK_ActivitySegments_Devices_DeviceId",
                table: "ActivitySegments",
                column: "DeviceId",
                principalTable: "Devices",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_ActivitySegments_Facts_FactKey",
                table: "ActivitySegments",
                column: "FactKey",
                principalTable: "Facts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InputEvents_Facts_FactKey",
                table: "InputEvents",
                column: "FactKey",
                principalTable: "Facts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql("""
                -- Existing table rows are migration evidence, not invented original Collector provenance.
                INSERT INTO "FactSubjects" ("OwnerId", "SubjectId", "Kind", "DeviceId", "DisplayName")
                SELECT DISTINCT d."OwnerId",
                  CASE WHEN d."HardwareId" ~ '^subject:(account|person):[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' THEN split_part(d."HardwareId", ':', 3)::uuid
                       ELSE md5('legacy-subject:' || d."OwnerId" || ':' || d."Id")::uuid END,
                  CASE WHEN d."HardwareId" ~ '^subject:(account|person):[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' THEN split_part(d."HardwareId", ':', 2) ELSE 'machine' END,
                  CASE WHEN d."HardwareId" ~ '^subject:(account|person):[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' THEN NULL ELSE d."Id" END,
                  d."DeviceName"
                FROM "Devices" d
                ON CONFLICT DO NOTHING;

                INSERT INTO "FactStreams" ("OwnerId", "StreamId", "SubjectId", "CollectorInstanceId", "OutputId", "Source", "FactKind", "SchemaId", "SchemaMajor", "Dimensions", "Origin")
                SELECT DISTINCT d."OwnerId", md5('legacy-stream:' || d."OwnerId" || ':' || d."Id" || ':' || s."Source" || ':segment')::uuid,
                  CASE WHEN d."HardwareId" ~ '^subject:(account|person):[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' THEN split_part(d."HardwareId", ':', 3)::uuid
                       ELSE md5('legacy-subject:' || d."OwnerId" || ':' || d."Id")::uuid END,
                  NULL::uuid, 'legacy-import', s."Source", 'segment', 'heartbeat.legacy.segment', 1, '{}'::jsonb, 'legacy-import'
                FROM "ActivitySegments" s JOIN "Devices" d ON d."Id" = s."DeviceId";

                INSERT INTO "FactStreams" ("OwnerId", "StreamId", "SubjectId", "CollectorInstanceId", "OutputId", "Source", "FactKind", "SchemaId", "SchemaMajor", "Dimensions", "Origin")
                SELECT DISTINCT d."OwnerId", md5('legacy-stream:' || d."OwnerId" || ':' || d."Id" || ':system:event')::uuid,
                  CASE WHEN d."HardwareId" ~ '^subject:(account|person):[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' THEN split_part(d."HardwareId", ':', 3)::uuid
                       ELSE md5('legacy-subject:' || d."OwnerId" || ':' || d."Id")::uuid END,
                  NULL::uuid, 'legacy-import', 'system', 'event', 'heartbeat.legacy.event', 1, '{}'::jsonb, 'legacy-import'
                FROM "InputEvents" e JOIN "Devices" d ON d."Id" = e."DeviceId";

                INSERT INTO "Facts" ("Id", "OwnerId", "StreamId", "FactId", "Revision", "SchemaRevision", "Origin", "Start", "End", "Payload", "ContentHash", "LegacyRecord", "LegacyId", "LegacyDeviceId", "LegacyKind")
                SELECT md5('legacy-fact:segment:' || s."Id")::uuid, d."OwnerId",
                  md5('legacy-stream:' || d."OwnerId" || ':' || d."Id" || ':' || s."Source" || ':segment')::uuid,
                  s."Id", 0, 0, 'legacy-import', (extract(epoch FROM s."StartTime") * 10000000)::bigint + 621355968000000000,
                  (extract(epoch FROM s."EndTime") * 10000000)::bigint + 621355968000000000,
                  CASE WHEN jsonb_typeof(s."Attributes") = 'object'
                             AND s."Attributes"->>'identityKey' = s."IdentityKey"
                             AND (s."Attributes"->>'title') IS NOT DISTINCT FROM s."Title"
                             AND jsonb_typeof(s."Attributes"->'attributes') = 'object'
                       THEN s."Attributes"
                       ELSE jsonb_build_object('identityKey', s."IdentityKey", 'title', s."Title", 'attributes', s."Attributes") END,
                  '', to_jsonb(s) - 'FactKey' - 'Payload' - 'OwnerId', s."Id", s."DeviceId", 'segment'
                FROM "ActivitySegments" s JOIN "Devices" d ON d."Id" = s."DeviceId";

                INSERT INTO "Facts" ("Id", "OwnerId", "StreamId", "FactId", "Revision", "SchemaRevision", "Origin", "OccurredAt", "Payload", "ContentHash", "LegacyRecord", "LegacyId", "LegacyDeviceId", "LegacyKind")
                SELECT md5('legacy-fact:event:' || e."Id")::uuid, d."OwnerId", md5('legacy-stream:' || d."OwnerId" || ':' || d."Id" || ':system:event')::uuid,
                  e."Id", 0, 0, 'legacy-import', (extract(epoch FROM e."Timestamp") * 10000000)::bigint + 621355968000000000,
                  jsonb_build_object('eventType', CASE e."EventType" WHEN 1 THEN 'keyDown' WHEN 2 THEN 'mouseButton' WHEN 3 THEN 'mouseScroll' END, 'codeSet', e."CodeSet", 'code', e."Code"),
                  '', to_jsonb(e) - 'FactKey', e."Id", e."DeviceId", 'event'
                FROM "InputEvents" e JOIN "Devices" d ON d."Id" = e."DeviceId";

                UPDATE "ActivitySegments" s SET "FactKey" = f."Id", "OwnerId" = f."OwnerId", "Payload" = f."Payload", "Attributes" = f."Payload"->'attributes', "DeviceId" = subject."DeviceId"
                FROM "Facts" f JOIN "FactStreams" stream ON stream."OwnerId" = f."OwnerId" AND stream."StreamId" = f."StreamId"
                JOIN "FactSubjects" subject ON subject."OwnerId" = stream."OwnerId" AND subject."SubjectId" = stream."SubjectId"
                WHERE f."LegacyKind" = 'segment' AND f."LegacyId" = s."Id";
                UPDATE "InputEvents" e SET "FactKey" = f."Id" FROM "Facts" f WHERE f."LegacyKind" = 'event' AND f."LegacyId" = e."Id";

                DO $migration$
                BEGIN
                  IF (SELECT count(*) FROM "ActivitySegments") <> (SELECT count(*) FROM "Facts" WHERE "LegacyKind" = 'segment')
                     OR (SELECT count(*) FROM "InputEvents") <> (SELECT count(*) FROM "Facts" WHERE "LegacyKind" = 'event') THEN
                    RAISE EXCEPTION 'Native Fact migration did not preserve every historical row';
                  END IF;
                END $migration$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $migration$
                BEGIN
                  IF EXISTS (SELECT 1 FROM "Facts" WHERE "Origin" = 'native') OR EXISTS (SELECT 1 FROM "FactGaps") THEN
                    RAISE EXCEPTION 'Cannot downgrade native Fact custody without losing data; restore the pre-upgrade backup instead';
                  END IF;
                END $migration$;
                UPDATE "ActivitySegments" s SET
                  "DeviceId" = f."LegacyDeviceId",
                  "Attributes" = CASE WHEN f."LegacyRecord"->'Attributes' = 'null'::jsonb THEN NULL ELSE f."LegacyRecord"->'Attributes' END
                FROM "Facts" f WHERE s."FactKey" = f."Id" AND f."LegacyRecord" IS NOT NULL;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_ActivitySegments_Devices_DeviceId",
                table: "ActivitySegments");

            migrationBuilder.DropForeignKey(
                name: "FK_ActivitySegments_Facts_FactKey",
                table: "ActivitySegments");

            migrationBuilder.DropForeignKey(
                name: "FK_InputEvents_Facts_FactKey",
                table: "InputEvents");

            migrationBuilder.DropTable(
                name: "FactGaps");

            migrationBuilder.DropTable(
                name: "Facts");

            migrationBuilder.DropTable(
                name: "FactSchemas");

            migrationBuilder.DropTable(
                name: "FactStreams");

            migrationBuilder.DropTable(
                name: "FactSubjects");

            migrationBuilder.DropIndex(
                name: "IX_InputEvents_FactKey",
                table: "InputEvents");

            migrationBuilder.DropIndex(
                name: "IX_ActivitySegments_FactKey",
                table: "ActivitySegments");

            migrationBuilder.DropColumn(
                name: "FactKey",
                table: "InputEvents");

            migrationBuilder.DropColumn(
                name: "FactKey",
                table: "ActivitySegments");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "ActivitySegments");

            migrationBuilder.DropColumn(
                name: "Payload",
                table: "ActivitySegments");

            migrationBuilder.AlterColumn<long>(
                name: "DeviceId",
                table: "ActivitySegments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ActivitySegments_Devices_DeviceId",
                table: "ActivitySegments",
                column: "DeviceId",
                principalTable: "Devices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
