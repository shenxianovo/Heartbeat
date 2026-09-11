using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Server.Migrations;

public partial class ObservationFacts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$ BEGIN
              IF EXISTS (SELECT 1 FROM "Segments" s JOIN "Events" e USING ("Id")) THEN
                RAISE EXCEPTION 'Independent Segment and Event rows share an Id; resolve the explicit identity mapping before combining them.';
              END IF;
            END $$;
            ALTER TABLE "Segments" RENAME TO "Facts";
            ALTER TABLE "Facts" RENAME COLUMN "Payload" TO "Result";
            ALTER TABLE "Facts" RENAME COLUMN "ObserverId" TO "CollectorId";
            ALTER TABLE "Facts" ADD COLUMN "Kind" varchar(13) NOT NULL DEFAULT 'segment';
            ALTER TABLE "Facts" ALTER COLUMN "Kind" DROP DEFAULT;
            ALTER TABLE "Facts" ALTER COLUMN "StartTime" DROP NOT NULL;
            ALTER TABLE "Facts" ALTER COLUMN "EndTime" DROP NOT NULL;
            ALTER TABLE "Facts" RENAME CONSTRAINT "PK_Segments" TO "PK_Facts";
            ALTER TABLE "Facts" RENAME CONSTRAINT "CK_Segments_Revision" TO "CK_Facts_Revision";
            ALTER TABLE "Facts" RENAME CONSTRAINT "FK_Segments_AppIdentities_AppIdentityId" TO "FK_Facts_AppIdentities_AppIdentityId";
            ALTER TABLE "Facts" RENAME CONSTRAINT "FK_Segments_Streams_OwnerId_StreamId" TO "FK_Facts_Streams_OwnerId_StreamId";
            ALTER INDEX "IX_Segments_AppIdentityId" RENAME TO "IX_Facts_AppIdentityId";
            ALTER INDEX "IX_Segments_OwnerId_TargetKind_TargetId" RENAME TO "IX_Facts_OwnerId_TargetKind_TargetId";
            ALTER INDEX "IX_Segments_OwnerId_Source_StartTime" RENAME TO "IX_Facts_OwnerId_Source_StartTime";
            ALTER INDEX "IX_Segments_OwnerId_FactId" RENAME TO "IX_Facts_OwnerId_FactId";
            DROP INDEX "IX_Segments_OwnerId_StreamId_FactId";

            INSERT INTO "Facts" ("Id", "OwnerId", "Kind", "StreamId", "FactId", "Revision", "CollectorId", "TargetKind", "TargetId", "Source", "AppIdentityId", "StartTime", "EndTime", "Result")
              SELECT "Id", "OwnerId", 'event', "StreamId", "FactId", "Revision", "ObserverId", "TargetKind", "TargetId", "Source", "AppIdentityId", "Timestamp", NULL, "Payload" FROM "Events";
            DROP TABLE "Events";
            CREATE UNIQUE INDEX "IX_Facts_OwnerId_Kind_StreamId_FactId" ON "Facts" ("OwnerId", "Kind", "StreamId", "FactId");
            CREATE INDEX "IX_Facts_OwnerId_StartTime" ON "Facts" ("OwnerId", "StartTime");
            CREATE INDEX "IX_Facts_OwnerId_StreamId" ON "Facts" ("OwnerId", "StreamId");
            ALTER TABLE "Facts" ADD CONSTRAINT "CK_Facts_Time" CHECK (
              "StartTime" IS NOT NULL AND isfinite("StartTime") AND
              (("Kind" = 'event' AND "EndTime" IS NULL) OR ("Kind" = 'segment' AND "EndTime" IS NOT NULL AND isfinite("EndTime") AND "EndTime" >= "StartTime")));

            CREATE OR REPLACE FUNCTION heartbeat_restrict_target_change() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE kind text;
            BEGIN
              IF TG_OP = 'UPDATE' AND NEW."Id" = OLD."Id" AND NEW."OwnerId" = OLD."OwnerId" THEN RETURN NEW; END IF;
              kind := CASE WHEN TG_TABLE_NAME = 'Devices' THEN 'device' WHEN TG_TABLE_NAME = 'ServiceAccounts' THEN 'account'
                WHEN TG_TABLE_NAME = 'Persons' THEN 'person' ELSE 'application-context' END;
              IF EXISTS (SELECT 1 FROM "Facts" WHERE "OwnerId" = OLD."OwnerId" AND "TargetKind" = kind AND "TargetId" = OLD."Id")
              THEN RAISE foreign_key_violation USING MESSAGE = 'Target is referenced by Facts'; END IF;
              IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
              RETURN NEW;
            END $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN RAISE EXCEPTION 'Fact storage rollback requires the pre-upgrade backup and custody of subsequently accepted facts.'; END $$;
        """);
}
