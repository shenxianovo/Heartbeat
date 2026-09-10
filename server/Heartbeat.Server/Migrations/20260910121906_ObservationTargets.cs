using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class ObservationTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ObserverId",
                table: "Segments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TargetId",
                table: "Segments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetKind",
                table: "Segments",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ObserverId",
                table: "Events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TargetId",
                table: "Events",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetKind",
                table: "Events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Segments_OwnerId_TargetKind_TargetId",
                table: "Segments",
                columns: new[] { "OwnerId", "TargetKind", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_Events_OwnerId_TargetKind_TargetId",
                table: "Events",
                columns: new[] { "OwnerId", "TargetKind", "TargetId" });
            // Evolve the deployed family tables in place. Missing historical collectors stay unknown.
            foreach (var table in new[] { "Segments", "Events" })
                migrationBuilder.Sql($$"""
                    UPDATE "{{table}}" f SET "TargetKind" = 'device', "TargetId" = subject."DeviceId",
                        "ObserverId" = CASE WHEN stream."Origin" = 'native' THEN stream."CollectorInstanceId" ELSE NULL END
                    FROM "Streams" stream JOIN "Subjects" subject
                      ON subject."OwnerId" = stream."OwnerId" AND subject."SubjectId" = stream."SubjectId"
                    WHERE f."OwnerId" = stream."OwnerId" AND f."StreamId" = stream."StreamId"
                      AND f."Source" = 'system' AND stream."Source" = 'system'
                      AND subject."Kind" = 'machine' AND subject."DeviceId" IS NOT NULL;
                    """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN RAISE EXCEPTION 'Observation Target rollback requires the pre-upgrade backup; dropping attribution is lossy.'; END $$;
                """);
        }
    }
}
