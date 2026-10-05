using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Heartbeat.Migrations
{
    /// <inheritdoc />
    public partial class InitialRecordingModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "objects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_objects", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "hubs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_hubs", x => x.id);
                    table.ForeignKey(
                        name: "FK_hubs_objects_id",
                        column: x => x.id,
                        principalTable: "objects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "object_bindings",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    identity_namespace = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    identity_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_object_bindings", x => x.id);
                    table.ForeignKey(
                        name: "FK_object_bindings_objects_object_id",
                        column: x => x.object_id,
                        principalTable: "objects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_object_bindings_objects_scope_id",
                        column: x => x.scope_id,
                        principalTable: "objects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

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
                    table.ForeignKey(
                        name: "FK_timelines_objects_id",
                        column: x => x.id,
                        principalTable: "objects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "collectors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    timeline_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    target = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    display_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collectors", x => x.id);
                    table.CheckConstraint("ck_collectors_display_name", "btrim(display_name) <> ''");
                    table.CheckConstraint("ck_collectors_key", "btrim(key) <> ''");
                    table.CheckConstraint("ck_collectors_target", "btrim(target) <> ''");
                    table.ForeignKey(
                        name: "FK_collectors_objects_id",
                        column: x => x.id,
                        principalTable: "objects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
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
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracks", x => x.id);
                    table.CheckConstraint("ck_tracks_time_mode", "time_mode IN ('point', 'range')");
                    table.CheckConstraint("ck_tracks_type", "btrim(type) <> ''");
                    table.CheckConstraint("ck_tracks_version", "version > 0");
                    table.ForeignKey(
                        name: "FK_tracks_collectors_collector_id",
                        column: x => x.collector_id,
                        principalTable: "collectors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tracks_objects_id",
                        column: x => x.id,
                        principalTable: "objects",
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
                    objects = table.Column<string>(type: "jsonb", nullable: false),
                    value = table.Column<JsonElement>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_records", x => x.id);
                    table.CheckConstraint("ck_records_time_range", "ended_at IS NULL OR ended_at >= started_at");
                    table.ForeignKey(
                        name: "FK_records_objects_id",
                        column: x => x.id,
                        principalTable: "objects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_records_tracks_track_id",
                        column: x => x.track_id,
                        principalTable: "tracks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "object_descriptions",
                columns: table => new
                {
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    record_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_object_descriptions", x => x.object_id);
                    table.ForeignKey(
                        name: "FK_object_descriptions_objects_object_id",
                        column: x => x.object_id,
                        principalTable: "objects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_object_descriptions_records_record_id",
                        column: x => x.record_id,
                        principalTable: "records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "record_objects",
                columns: table => new
                {
                    record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reference_index = table.Column<int>(type: "integer", nullable: false),
                    object_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_record_objects", x => new { x.record_id, x.reference_index });
                    table.ForeignKey(
                        name: "FK_record_objects_objects_object_id",
                        column: x => x.object_id,
                        principalTable: "objects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_record_objects_records_record_id",
                        column: x => x.record_id,
                        principalTable: "records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_collectors_timeline_id_key_target",
                table: "collectors",
                columns: new[] { "timeline_id", "key", "target" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_object_bindings_object_id",
                table: "object_bindings",
                column: "object_id");

            migrationBuilder.CreateIndex(
                name: "IX_object_bindings_owner_id_scope_id_identity_namespace_identi~",
                table: "object_bindings",
                columns: new[] { "owner_id", "scope_id", "identity_namespace", "identity_key" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_object_bindings_scope_id",
                table: "object_bindings",
                column: "scope_id");

            migrationBuilder.CreateIndex(
                name: "IX_object_descriptions_record_id",
                table: "object_descriptions",
                column: "record_id");

            migrationBuilder.CreateIndex(
                name: "IX_objects_owner_id",
                table: "objects",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_record_objects_object_id_record_id",
                table: "record_objects",
                columns: new[] { "object_id", "record_id" });

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
                name: "hubs");

            migrationBuilder.DropTable(
                name: "object_bindings");

            migrationBuilder.DropTable(
                name: "object_descriptions");

            migrationBuilder.DropTable(
                name: "record_objects");

            migrationBuilder.DropTable(
                name: "records");

            migrationBuilder.DropTable(
                name: "tracks");

            migrationBuilder.DropTable(
                name: "collectors");

            migrationBuilder.DropTable(
                name: "timelines");

            migrationBuilder.DropTable(
                name: "objects");
        }
    }
}
