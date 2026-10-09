using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Infrastructure.Database.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "entities",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    table_name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entities", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "entity_data",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    data = table.Column<JsonElement>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entity_data", x => x.id);
                    table.ForeignKey(
                        name: "fk_entity_data_entities_id",
                        column: x => x.id,
                        principalTable: "entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "observation_schemas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    schema = table.Column<JsonElement>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_observation_schemas", x => x.id);
                    table.ForeignKey(
                        name: "fk_observation_schemas_entities_id",
                        column: x => x.id,
                        principalTable: "entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "observations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    observer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    data_id = table.Column<Guid>(type: "uuid", nullable: false),
                    schema_id = table.Column<Guid>(type: "uuid", nullable: false),
                    start_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    end_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    time_zone = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_observations", x => x.id);
                    table.CheckConstraint("ck_observations_time_order", "start_at <= end_at");
                    table.ForeignKey(
                        name: "fk_observations_entities_id",
                        column: x => x.id,
                        principalTable: "entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "observers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_observers", x => x.id);
                    table.ForeignKey(
                        name: "fk_observers_entities_id",
                        column: x => x.id,
                        principalTable: "entities",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entity_data");

            migrationBuilder.DropTable(
                name: "observation_schemas");

            migrationBuilder.DropTable(
                name: "observations");

            migrationBuilder.DropTable(
                name: "observers");

            migrationBuilder.DropTable(
                name: "entities");
        }
    }
}
