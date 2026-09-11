using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class ExplicitFactAspects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Facts_OwnerId_Aspect_StartTime",
                table: "Facts",
                columns: new[] { "OwnerId", "Aspect", "StartTime" },
                filter: "\"Kind\" = 'segment'");
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION heartbeat_fact_observation() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."CollectorId" IS NOT NULL THEN
                    INSERT INTO "Collectors" ("OwnerId", "Id", "Kind") VALUES (NEW."OwnerId", NEW."CollectorId", NEW."Source")
                      ON CONFLICT ("OwnerId", "Id") DO NOTHING;
                  END IF;
                  NEW."FoiId" := (SELECT foi FROM heartbeat_fact_objects(NEW."OwnerId", NEW."TargetKind", NEW."TargetId", NEW."AppIdentityId"));
                  -- Only the pre-Aspect input boundary interprets old source/payload shapes.
                  IF NEW."Aspect" IS NULL THEN
                    NEW."Aspect" := heartbeat_fact_aspect(NEW."Source", NEW."Kind", NEW."Result");
                  END IF;
                  RETURN NEW;
                END $$;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
            DO $$ BEGIN RAISE EXCEPTION 'Explicit Aspect custody cannot be downgraded to source-based inference; restore with accepted-fact custody.'; END $$;
            """);
    }
}
