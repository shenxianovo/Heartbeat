namespace Heartbeat.Server.Migrations;

public partial class ObservationObjects
{
    // Frozen with this migration: live writers and the one-time backfill use the same conversion.
    private const string StorageSql = """
        CREATE FUNCTION heartbeat_object_record() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE object_kind text; object_scope text; object_key text; object_name text; object_owner text;
        BEGIN
          object_owner := to_jsonb(NEW)->>'OwnerId';
          IF TG_OP = 'UPDATE' AND (to_jsonb(NEW)->>'OwnerId') IS DISTINCT FROM (to_jsonb(OLD)->>'OwnerId') THEN
            RAISE check_violation USING MESSAGE = 'Business object ownership cannot change';
          END IF;
          CASE TG_TABLE_NAME
            WHEN 'Devices' THEN
              IF NEW."HardwareId" LIKE 'subject:account:%' OR NEW."HardwareId" LIKE 'subject:person:%' THEN RETURN NEW; END IF;
              object_kind := 'machine'; object_scope := 'heartbeat.device'; object_key := NEW."HardwareId"; object_name := NEW."DeviceName";
              IF object_key = '' THEN object_scope := 'heartbeat.device-id'; object_key := NEW."Id"::text; END IF;
            WHEN 'Apps' THEN
              object_kind := 'app'; object_scope := 'heartbeat.app'; object_key := NEW."Key"; object_name := NEW."DisplayName";
            WHEN 'ServiceAccounts' THEN
              object_kind := 'account'; object_scope := NEW."ServiceKey";
              object_key := COALESCE(NEW."ServiceAccountId", NEW."LegacySubjectId"::text); object_name := NEW."ServiceAccountId";
            WHEN 'Persons' THEN
              object_kind := 'person'; object_scope := 'heartbeat.person'; object_key := NEW."Reference"::text; object_name := NULL;
          END CASE;
          IF TG_OP = 'UPDATE' AND OLD."ObjectId" IS NOT NULL AND NEW."ObjectId" IS DISTINCT FROM OLD."ObjectId" THEN
            RAISE check_violation USING MESSAGE = 'Business object reference cannot be reassigned';
          END IF;
          IF NEW."ObjectId" IS NULL THEN
            IF object_owner IS NULL THEN
              INSERT INTO "Objects" ("OwnerId", "Kind", "Scope", "Key", "Name") VALUES (NULL, object_kind, object_scope, object_key, object_name)
                ON CONFLICT ("Kind", "Scope", "Key") WHERE "OwnerId" IS NULL DO UPDATE SET "Name" = EXCLUDED."Name" RETURNING "Id" INTO NEW."ObjectId";
            ELSE
              INSERT INTO "Objects" ("OwnerId", "Kind", "Scope", "Key", "Name") VALUES (object_owner, object_kind, object_scope, object_key, object_name)
                ON CONFLICT ("OwnerId", "Kind", "Scope", "Key") WHERE "OwnerId" IS NOT NULL DO UPDATE SET "Name" = EXCLUDED."Name" RETURNING "Id" INTO NEW."ObjectId";
            END IF;
          ELSE
            UPDATE "Objects" SET "Scope" = object_scope, "Key" = object_key, "Name" = object_name
              WHERE "Id" = NEW."ObjectId" AND "Kind" = object_kind AND "OwnerId" IS NOT DISTINCT FROM object_owner
                AND ("Scope", "Key", "Name") IS DISTINCT FROM (object_scope, object_key, object_name);
            IF NOT EXISTS (SELECT 1 FROM "Objects" WHERE "Id" = NEW."ObjectId" AND "Kind" = object_kind AND "OwnerId" IS NOT DISTINCT FROM object_owner) THEN
              RAISE foreign_key_violation USING MESSAGE = 'Object must match its business identity and Owner';
            END IF;
          END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER "Devices_Object" BEFORE INSERT OR UPDATE ON "Devices" FOR EACH ROW EXECUTE FUNCTION heartbeat_object_record();
        CREATE TRIGGER "Apps_Object" BEFORE INSERT OR UPDATE ON "Apps" FOR EACH ROW EXECUTE FUNCTION heartbeat_object_record();
        CREATE TRIGGER "Accounts_Object" BEFORE INSERT OR UPDATE ON "ServiceAccounts" FOR EACH ROW EXECUTE FUNCTION heartbeat_object_record();
        CREATE TRIGGER "Persons_Object" BEFORE INSERT OR UPDATE ON "Persons" FOR EACH ROW EXECUTE FUNCTION heartbeat_object_record();
        UPDATE "Devices" SET "ObjectId" = "ObjectId";
        UPDATE "Apps" SET "ObjectId" = "ObjectId";
        UPDATE "ServiceAccounts" SET "ObjectId" = "ObjectId";
        UPDATE "Persons" SET "ObjectId" = "ObjectId";

        CREATE FUNCTION heartbeat_fact_objects(fact_owner text, target_kind text, target_id bigint, identity_id bigint)
          RETURNS TABLE(foi uuid, device uuid, app uuid) LANGUAGE sql STABLE AS $$
          SELECT CASE target_kind WHEN 'device' THEN d."ObjectId" WHEN 'application-context' THEN ca."ObjectId"
            WHEN 'account' THEN a."ObjectId" WHEN 'person' THEN p."ObjectId" END,
            d."ObjectId", COALESCE(ca."ObjectId", ia."ObjectId")
          FROM (SELECT 1) seed
          LEFT JOIN "ApplicationContexts" c ON target_kind = 'application-context' AND c."OwnerId" = fact_owner AND c."Id" = target_id
          LEFT JOIN "Devices" d ON d."OwnerId" = fact_owner AND d."Id" = CASE WHEN target_kind = 'device' THEN target_id ELSE c."DeviceId" END
          LEFT JOIN "Apps" ca ON ca."Id" = c."AppId"
          LEFT JOIN "ServiceAccounts" a ON target_kind = 'account' AND a."OwnerId" = fact_owner AND a."Id" = target_id
          LEFT JOIN "Persons" p ON target_kind = 'person' AND p."OwnerId" = fact_owner AND p."Id" = target_id
          LEFT JOIN "AppIdentities" i ON i."Id" = identity_id
          LEFT JOIN "Apps" ia ON ia."Id" = i."AppId";
        $$;
        CREATE FUNCTION heartbeat_fact_aspect(source text, family text, result jsonb) RETURNS text LANGUAGE sql IMMUTABLE AS $$
          SELECT CASE
            WHEN family = 'segment' AND jsonb_typeof(result->'activityKey') = 'string' THEN
              CASE source WHEN 'system' THEN 'desktop-activity' WHEN 'browser' THEN 'selected-page' WHEN 'vrchat.account' THEN 'account-location' ELSE 'activity' END
            WHEN family = 'event' AND result->>'eventType' IN ('keyDown','mouseButton','mouseScroll') THEN 'input'
            ELSE NULL END;
        $$;

        INSERT INTO "Collectors" ("OwnerId", "Id", "Kind")
          SELECT "OwnerId", "CollectorId", CASE WHEN count(DISTINCT "Source") = 1 THEN min("Source") END
          FROM "Facts" WHERE "CollectorId" IS NOT NULL GROUP BY "OwnerId", "CollectorId";
        UPDATE "Facts" f SET "FoiId" = (SELECT foi FROM heartbeat_fact_objects(f."OwnerId", f."TargetKind", f."TargetId", f."AppIdentityId")),
          "Aspect" = heartbeat_fact_aspect(f."Source", f."Kind", f."Result");

        INSERT INTO "Relations" ("OwnerId", "Kind", "ValidFrom", "ValidTo", "Evidence")
          SELECT f."OwnerId", 'observed-on', f."StartTime", COALESCE(f."EndTime", f."StartTime"), jsonb_build_object('factId', f."Id")
          FROM "Facts" f CROSS JOIN LATERAL heartbeat_fact_objects(f."OwnerId", f."TargetKind", f."TargetId", f."AppIdentityId") o
          WHERE o.device IS NOT NULL AND o.app IS NOT NULL;
        INSERT INTO "RelationMembers" ("RelationId", "Role", "ObjectId")
          SELECT r."Id", m.role, m.object_id FROM "Relations" r JOIN "Facts" f ON r."FactId" = f."Id"
          CROSS JOIN LATERAL heartbeat_fact_objects(f."OwnerId", f."TargetKind", f."TargetId", f."AppIdentityId") o
          CROSS JOIN LATERAL (VALUES ('device', o.device), ('app', o.app)) m(role, object_id);
        INSERT INTO "Relations" ("OwnerId", "Kind", "ValidFrom", "ValidTo", "Evidence")
          SELECT "OwnerId", 'used-by', "Start", "End", jsonb_build_object('associationId', "Id") FROM "PersonAssociations";
        INSERT INTO "RelationMembers" ("RelationId", "Role", "ObjectId")
          SELECT r."Id", m.role, m.object_id FROM "Relations" r JOIN "PersonAssociations" a ON a."Id" = r."AssociationId"
          JOIN "Persons" p ON p."Id" = a."PersonId" AND p."OwnerId" = a."OwnerId"
          LEFT JOIN "Devices" d ON d."Id" = a."DeviceId" AND d."OwnerId" = a."OwnerId"
          LEFT JOIN "ServiceAccounts" c ON c."Id" = a."AccountId" AND c."OwnerId" = a."OwnerId"
          CROSS JOIN LATERAL (VALUES ('person', p."ObjectId"), (CASE WHEN a."DeviceId" IS NOT NULL THEN 'device' ELSE 'account' END, COALESCE(d."ObjectId", c."ObjectId"))) m(role, object_id);

        CREATE FUNCTION heartbeat_fact_observation() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF NEW."CollectorId" IS NOT NULL THEN
            INSERT INTO "Collectors" ("OwnerId", "Id", "Kind") VALUES (NEW."OwnerId", NEW."CollectorId", NEW."Source")
              ON CONFLICT ("OwnerId", "Id") DO NOTHING;
          END IF;
          NEW."FoiId" := (SELECT foi FROM heartbeat_fact_objects(NEW."OwnerId", NEW."TargetKind", NEW."TargetId", NEW."AppIdentityId"));
          NEW."Aspect" := heartbeat_fact_aspect(NEW."Source", NEW."Kind", NEW."Result");
          RETURN NEW;
        END $$;
        CREATE TRIGGER "Facts_Observation" BEFORE INSERT OR UPDATE ON "Facts" FOR EACH ROW EXECUTE FUNCTION heartbeat_fact_observation();

        CREATE FUNCTION heartbeat_fact_relation() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE device_object uuid; app_object uuid; relation_id uuid;
        BEGIN
          SELECT device, app INTO device_object, app_object FROM heartbeat_fact_objects(NEW."OwnerId", NEW."TargetKind", NEW."TargetId", NEW."AppIdentityId");
          IF device_object IS NULL OR app_object IS NULL THEN
            DELETE FROM "Relations" WHERE "FactId" = NEW."Id" AND "Kind" = 'observed-on';
            RETURN NEW;
          END IF;
          INSERT INTO "Relations" ("OwnerId", "Kind", "ValidFrom", "ValidTo", "Evidence")
            VALUES (NEW."OwnerId", 'observed-on', NEW."StartTime", COALESCE(NEW."EndTime", NEW."StartTime"), jsonb_build_object('factId', NEW."Id"))
            ON CONFLICT ("OwnerId", "Kind", "FactId") WHERE "FactId" IS NOT NULL
            DO UPDATE SET "ValidFrom" = EXCLUDED."ValidFrom", "ValidTo" = EXCLUDED."ValidTo" RETURNING "Id" INTO relation_id;
          DELETE FROM "RelationMembers" WHERE "RelationId" = relation_id;
          INSERT INTO "RelationMembers" ("RelationId", "Role", "ObjectId") VALUES (relation_id, 'device', device_object), (relation_id, 'app', app_object);
          RETURN NEW;
        END $$;
        CREATE TRIGGER "Facts_Relation" AFTER INSERT OR UPDATE ON "Facts" FOR EACH ROW EXECUTE FUNCTION heartbeat_fact_relation();

        CREATE FUNCTION heartbeat_association_relation() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE relation_id uuid; person_object uuid; used_object uuid;
        BEGIN
          SELECT "ObjectId" INTO person_object FROM "Persons" WHERE "Id" = NEW."PersonId" AND "OwnerId" = NEW."OwnerId";
          IF NEW."DeviceId" IS NOT NULL THEN
            SELECT "ObjectId" INTO used_object FROM "Devices" WHERE "Id" = NEW."DeviceId" AND "OwnerId" = NEW."OwnerId";
          ELSE
            SELECT "ObjectId" INTO used_object FROM "ServiceAccounts" WHERE "Id" = NEW."AccountId" AND "OwnerId" = NEW."OwnerId";
          END IF;
          IF person_object IS NULL OR used_object IS NULL THEN
            RAISE foreign_key_violation USING MESSAGE = 'Usage association members must exist within their Owner';
          END IF;
          INSERT INTO "Relations" ("OwnerId", "Kind", "ValidFrom", "ValidTo", "Evidence")
            VALUES (NEW."OwnerId", 'used-by', NEW."Start", NEW."End", jsonb_build_object('associationId', NEW."Id"))
            ON CONFLICT ("OwnerId", "Kind", "AssociationId") WHERE "AssociationId" IS NOT NULL
            DO UPDATE SET "ValidFrom" = EXCLUDED."ValidFrom", "ValidTo" = EXCLUDED."ValidTo" RETURNING "Id" INTO relation_id;
          DELETE FROM "RelationMembers" WHERE "RelationId" = relation_id;
          INSERT INTO "RelationMembers" ("RelationId", "Role", "ObjectId") VALUES
            (relation_id, 'person', person_object), (relation_id, CASE WHEN NEW."DeviceId" IS NOT NULL THEN 'device' ELSE 'account' END, used_object);
          RETURN NEW;
        END $$;
        CREATE TRIGGER "Associations_Relation" AFTER INSERT OR UPDATE ON "PersonAssociations" FOR EACH ROW EXECUTE FUNCTION heartbeat_association_relation();

        CREATE FUNCTION heartbeat_identity_observations() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF NEW."AppId" IS DISTINCT FROM OLD."AppId" THEN
            UPDATE "Facts" SET "AppIdentityId" = NEW."Id" WHERE "AppIdentityId" = NEW."Id";
          END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER "AppIdentities_Observations" AFTER UPDATE OF "AppId" ON "AppIdentities" FOR EACH ROW EXECUTE FUNCTION heartbeat_identity_observations();

        -- Updating the parent tuple serializes member edits, including repeatable-read transactions.
        CREATE FUNCTION heartbeat_relation_member_write() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF TG_OP = 'UPDATE' AND NEW."RelationId" <> OLD."RelationId" THEN
            RAISE check_violation USING MESSAGE = 'Relation members cannot move to another relation';
          END IF;
          UPDATE "Relations" SET "Evidence" = "Evidence"
            WHERE "Id" = CASE WHEN TG_OP = 'DELETE' THEN OLD."RelationId" ELSE NEW."RelationId" END;
          IF TG_OP = 'DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF;
        END $$;
        CREATE TRIGGER "Members_Write" BEFORE INSERT OR UPDATE OR DELETE ON "RelationMembers"
          FOR EACH ROW EXECUTE FUNCTION heartbeat_relation_member_write();

        CREATE FUNCTION heartbeat_relation_members_valid() RETURNS trigger LANGUAGE plpgsql AS $$
        DECLARE relation_id uuid; relation_owner text; relation_kind text; member_count int; device_count int; app_count int; person_count int; account_count int;
        BEGIN
          IF TG_TABLE_NAME = 'Relations' THEN relation_id := CASE WHEN TG_OP = 'DELETE' THEN OLD."Id" ELSE NEW."Id" END;
          ELSE relation_id := CASE WHEN TG_OP = 'DELETE' THEN OLD."RelationId" ELSE NEW."RelationId" END; END IF;
          SELECT "OwnerId", "Kind" INTO relation_owner, relation_kind FROM "Relations" WHERE "Id" = relation_id FOR UPDATE;
          IF NOT FOUND THEN RETURN NULL; END IF;
          IF relation_kind = 'observed-on' AND (SELECT "FactId" IS NULL FROM "Relations" WHERE "Id" = relation_id) THEN
            RAISE check_violation USING MESSAGE = 'Observed-on relations require an exact fact reference';
          END IF;
          IF EXISTS (SELECT 1 FROM "RelationMembers" m JOIN "Objects" o ON o."Id" = m."ObjectId" WHERE m."RelationId" = relation_id
            AND ((o."OwnerId" IS NOT NULL AND o."OwnerId" <> relation_owner) OR
              NOT ((m."Role" = 'device' AND o."Kind" = 'machine') OR (m."Role" = 'app' AND o."Kind" = 'app') OR
                (m."Role" = 'account' AND o."Kind" = 'account') OR (m."Role" = 'person' AND o."Kind" = 'person')))) THEN
            RAISE foreign_key_violation USING MESSAGE = 'Relation members must have the correct role and Owner';
          END IF;
          SELECT count(*), count(*) FILTER (WHERE "Role"='device'), count(*) FILTER (WHERE "Role"='app'),
            count(*) FILTER (WHERE "Role"='person'), count(*) FILTER (WHERE "Role"='account')
            INTO member_count, device_count, app_count, person_count, account_count FROM "RelationMembers" WHERE "RelationId" = relation_id;
          IF NOT ((relation_kind IN ('observed-on','installed-on') AND member_count=2 AND device_count=1 AND app_count=1)
            OR (relation_kind='application-account-use' AND member_count=3 AND device_count=1 AND app_count=1 AND account_count=1)
            OR (relation_kind='used-by' AND member_count=2 AND person_count=1 AND device_count+account_count=1)) THEN
            RAISE check_violation USING MESSAGE = 'Relation has unsupported or incomplete members';
          END IF;
          RETURN NULL;
        END $$;
        CREATE CONSTRAINT TRIGGER "Relations_Members" AFTER INSERT OR UPDATE ON "Relations" DEFERRABLE INITIALLY DEFERRED
          FOR EACH ROW EXECUTE FUNCTION heartbeat_relation_members_valid();
        CREATE CONSTRAINT TRIGGER "Members_Valid" AFTER INSERT OR UPDATE OR DELETE ON "RelationMembers" DEFERRABLE INITIALLY DEFERRED
          FOR EACH ROW EXECUTE FUNCTION heartbeat_relation_members_valid();

        CREATE FUNCTION heartbeat_object_identity() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
          IF (NEW."Id", NEW."Kind", NEW."OwnerId") IS DISTINCT FROM (OLD."Id", OLD."Kind", OLD."OwnerId") THEN
            RAISE check_violation USING MESSAGE = 'Object kind and ownership cannot change';
          END IF;
          RETURN NEW;
        END $$;
        CREATE TRIGGER "Objects_Identity" BEFORE UPDATE ON "Objects" FOR EACH ROW EXECUTE FUNCTION heartbeat_object_identity();
        """;
}
