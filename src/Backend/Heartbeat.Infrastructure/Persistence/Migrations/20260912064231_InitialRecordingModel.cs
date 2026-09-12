using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialRecordingModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "timelines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_timelines", x => x.id);
                    table.CheckConstraint("ck_timelines_display_name", "btrim(display_name) <> ''");
                });

            migrationBuilder.CreateTable(
                name: "collectors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    timeline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    target = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collectors", x => x.id);
                    table.CheckConstraint("ck_collectors_display_name", "btrim(display_name) <> ''");
                    table.CheckConstraint("ck_collectors_key", "btrim(key) <> ''");
                    table.CheckConstraint("ck_collectors_target", "btrim(target) <> ''");
                    table.ForeignKey(
                        name: "FK_collectors_timelines_timeline_id",
                        column: x => x.timeline_id,
                        principalTable: "timelines",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tracks",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    collector_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    time_mode = table.Column<string>(type: "text", nullable: false),
                    end_mode = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracks", x => x.id);
                    table.CheckConstraint("ck_tracks_time_mode", "(time_mode = 'point' AND end_mode IS NULL) OR (time_mode = 'range' AND end_mode IN ('explicit', 'next_record'))");
                    table.CheckConstraint("ck_tracks_type", "btrim(type) <> ''");
                    table.CheckConstraint("ck_tracks_version", "version > 0");
                    table.ForeignKey(
                        name: "FK_tracks_collectors_collector_id",
                        column: x => x.collector_id,
                        principalTable: "collectors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    track_id = table.Column<Guid>(type: "uuid", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    value = table.Column<JsonElement>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_records", x => x.id);
                    table.CheckConstraint("ck_records_time_range", "ended_at IS NULL OR ended_at >= started_at");
                    table.ForeignKey(
                        name: "FK_records_tracks_track_id",
                        column: x => x.track_id,
                        principalTable: "tracks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_collectors_timeline_id_key_target",
                table: "collectors",
                columns: new[] { "timeline_id", "key", "target" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_records_track_time",
                table: "records",
                columns: new[] { "track_id", "started_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_timelines_owner_id",
                table: "timelines",
                column: "owner_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tracks_collector_id_type_version",
                table: "tracks",
                columns: new[] { "collector_id", "type", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "records");

            migrationBuilder.DropTable(
                name: "tracks");

            migrationBuilder.DropTable(
                name: "collectors");

            migrationBuilder.DropTable(
                name: "timelines");
        }
    }
}
