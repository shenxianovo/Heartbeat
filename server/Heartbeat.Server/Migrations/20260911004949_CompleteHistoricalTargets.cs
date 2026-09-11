using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Server.Migrations;

/// <summary>Preserve the remaining evidenced device attribution before retiring query fallbacks.</summary>
public partial class CompleteHistoricalTargets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        foreach (var table in new[] { "Segments", "Events" })
            migrationBuilder.Sql($$"""
                UPDATE "{{table}}" f SET "TargetKind" = 'device', "TargetId" = subject."DeviceId"
                FROM "Streams" stream JOIN "Subjects" subject
                  ON subject."OwnerId" = stream."OwnerId" AND subject."SubjectId" = stream."SubjectId"
                WHERE f."OwnerId" = stream."OwnerId" AND f."StreamId" = stream."StreamId"
                  AND f."TargetKind" IS NULL AND f."TargetId" IS NULL
                  AND subject."Kind" = 'machine' AND subject."DeviceId" IS NOT NULL;
                """);
        // Other historical identities remain explicitly unknown. Current collector configuration
        // cannot recover an old Observer, service account, App, or Person.
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DO $$ BEGIN RAISE EXCEPTION 'Target cutover rollback requires the pre-upgrade backup.'; END $$;
        """);
}
