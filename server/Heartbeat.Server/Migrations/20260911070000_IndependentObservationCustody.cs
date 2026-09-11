using Heartbeat.Server.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Heartbeat.Server.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260911070000_IndependentObservationCustody")]
public sealed class IndependentObservationCustody : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        -- Existing delivery identities and their unique index remain usable by legacy producers.
        -- Independent Facts use the producer UUID already protected by PK_Facts.
        ALTER TABLE "Facts" ALTER COLUMN "StreamId" DROP NOT NULL;
        ALTER TABLE "Facts" ALTER COLUMN "FactId" DROP NOT NULL;
        ALTER TABLE "Facts" ALTER COLUMN "Source" DROP NOT NULL;
        ALTER TABLE "Facts" ADD COLUMN "AppReferenceEvidence" jsonb;
        ALTER TABLE "Facts" ADD CONSTRAINT "CK_Facts_LegacyIdentity"
          CHECK (("StreamId" IS NULL) = ("FactId" IS NULL));
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN RAISE EXCEPTION 'Independent observations cannot be assigned invented delivery identities; restore with accepted-fact custody.'; END $$;
        """);
}
