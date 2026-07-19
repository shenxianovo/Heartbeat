using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddStrandKnowledgeLayer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MutedHandles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Token = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MutedHandles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Strands",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Gloss = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Strands", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StrandHandles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StrandId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Token = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrandHandles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StrandHandles_Strands_StrandId",
                        column: x => x.StrandId,
                        principalTable: "Strands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MutedHandles_OwnerId_Source_Token",
                table: "MutedHandles",
                columns: new[] { "OwnerId", "Source", "Token" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StrandHandles_Source_Token",
                table: "StrandHandles",
                columns: new[] { "Source", "Token" });

            migrationBuilder.CreateIndex(
                name: "IX_StrandHandles_StrandId_Source_Token",
                table: "StrandHandles",
                columns: new[] { "StrandId", "Source", "Token" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Strands_OwnerId_Name",
                table: "Strands",
                columns: new[] { "OwnerId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MutedHandles");

            migrationBuilder.DropTable(
                name: "StrandHandles");

            migrationBuilder.DropTable(
                name: "Strands");
        }
    }
}
