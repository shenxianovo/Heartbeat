using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Heartbeat.Server.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260911060000_DirectObservations")]
public sealed class DirectObservations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP TRIGGER "Facts_Relation" ON "Facts";
        DROP FUNCTION heartbeat_fact_relation();
        DROP TRIGGER "AppIdentities_Observations" ON "AppIdentities";
        DROP FUNCTION heartbeat_identity_observations();
        DROP TRIGGER "Segments_Target" ON "Facts";
        DROP FUNCTION heartbeat_check_fact_target();
        DROP TRIGGER "Associations_Relation" ON "PersonAssociations";
        DROP FUNCTION heartbeat_association_relation();
        DROP FUNCTION heartbeat_fact_objects(text, text, bigint, bigint);

        ALTER TABLE "Relations" DROP CONSTRAINT "FK_Relations_PersonAssociations_OwnerId_AssociationId";
        ALTER TABLE "Relations" DROP COLUMN "AssociationId";
        ALTER TABLE "Relations" DROP CONSTRAINT "CK_Relations_Time";
        ALTER TABLE "Relations" ADD CONSTRAINT "CK_Relations_Time" CHECK (
          ("ValidFrom" IS NULL OR isfinite("ValidFrom")) AND ("ValidTo" IS NULL OR isfinite("ValidTo")) AND
          ("ValidFrom" IS NULL OR "ValidTo" IS NULL OR "ValidFrom" <= "ValidTo") AND
          ("Kind" <> 'used-by' OR "ValidFrom" IS NULL OR "ValidTo" IS NULL OR "ValidFrom" < "ValidTo"));
        DROP TABLE "PersonAssociations";
        DROP TABLE "ApplicationContexts";
        ALTER TABLE "Devices" DROP CONSTRAINT "AK_Devices_OwnerId_Id";
        ALTER TABLE "ServiceAccounts" DROP CONSTRAINT "AK_ServiceAccounts_OwnerId_Id";
        ALTER TABLE "Persons" DROP CONSTRAINT "AK_Persons_OwnerId_Id";

        -- Keep metadata deletion consistent with references added in older repeatable-read snapshots.
        CREATE FUNCTION heartbeat_touch_object_reference(object_id uuid) RETURNS void LANGUAGE plpgsql AS $$
        DECLARE object_kind text;
        BEGIN
          SELECT "Kind" INTO object_kind FROM "Objects" WHERE "Id" = object_id;
          CASE object_kind
            WHEN 'machine' THEN UPDATE "Devices" SET "DeviceName" = "DeviceName" WHERE "ObjectId" = object_id;
            WHEN 'account' THEN UPDATE "ServiceAccounts" SET "ServiceKey" = "ServiceKey" WHERE "ObjectId" = object_id;
            WHEN 'person' THEN UPDATE "Persons" SET "Reference" = "Reference" WHERE "ObjectId" = object_id;
            ELSE NULL;
          END CASE;
        END $$;
        CREATE OR REPLACE FUNCTION heartbeat_fact_observation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF NEW."CollectorId" IS NOT NULL THEN
            INSERT INTO "Collectors" ("OwnerId", "Id", "Kind") VALUES (NEW."OwnerId", NEW."CollectorId", NEW."Source")
              ON CONFLICT ("OwnerId", "Id") DO NOTHING;
          END IF;
          IF NEW."FoiId" IS NOT NULL THEN
            PERFORM 1 FROM "Objects" WHERE "Id" = NEW."FoiId" AND ("OwnerId" = NEW."OwnerId" OR ("OwnerId" IS NULL AND "Kind" = 'app')) FOR KEY SHARE;
            IF NOT FOUND THEN RAISE foreign_key_violation USING MESSAGE = 'Fact FOI must exist within its Owner or be a global App'; END IF;
            PERFORM heartbeat_touch_object_reference(NEW."FoiId");
          END IF;
          RETURN NEW;
        END $$;
        CREATE OR REPLACE FUNCTION heartbeat_restrict_target_change() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF TG_OP = 'UPDATE' AND NEW."Id" = OLD."Id" AND NEW."OwnerId" = OLD."OwnerId" THEN RETURN NEW; END IF;
          IF EXISTS (SELECT 1 FROM "Facts" WHERE "FoiId" = OLD."ObjectId")
            OR EXISTS (SELECT 1 FROM "RelationMembers" WHERE "ObjectId" = OLD."ObjectId") THEN
            RAISE foreign_key_violation USING MESSAGE = 'Object metadata is referenced by observations';
          END IF;
          IF TG_OP = 'DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF;
        END $$;
        ALTER FUNCTION heartbeat_restrict_target_change() RENAME TO heartbeat_restrict_object_metadata_change;
        ALTER TRIGGER "Devices_TargetReferenced" ON "Devices" RENAME TO "Devices_ObservationReferenced";
        CREATE OR REPLACE FUNCTION heartbeat_relation_member_write() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF TG_OP = 'UPDATE' AND NEW."RelationId" <> OLD."RelationId" THEN
            RAISE check_violation USING MESSAGE = 'Relation members cannot move to another relation';
          END IF;
          UPDATE "Relations" SET "Evidence" = "Evidence"
            WHERE "Id" = CASE WHEN TG_OP = 'DELETE' THEN OLD."RelationId" ELSE NEW."RelationId" END;
          IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
          PERFORM heartbeat_touch_object_reference(NEW."ObjectId");
          RETURN NEW;
        END $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN RAISE EXCEPTION 'Direct observation custody cannot be downgraded to Target-derived facts; restore with accepted-fact custody.'; END $$;
        """);
}
