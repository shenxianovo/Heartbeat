using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class MatcherKnowledgeLayer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MutedHandles");

            migrationBuilder.DropTable(
                name: "StrandHandles");

            migrationBuilder.DropTable(
                name: "TriageDecisions");

            migrationBuilder.CreateTable(
                name: "MutedMatchers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StepsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MutedMatchers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StrandMatchers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StrandId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StepsJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StrandMatchers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StrandMatchers_Strands_StrandId",
                        column: x => x.StrandId,
                        principalTable: "Strands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MutedMatchers_OwnerId_Source_StepsJson",
                table: "MutedMatchers",
                columns: new[] { "OwnerId", "Source", "StepsJson" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StrandMatchers_StrandId_Source_StepsJson",
                table: "StrandMatchers",
                columns: new[] { "StrandId", "Source", "StepsJson" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MutedMatchers");

            migrationBuilder.DropTable(
                name: "StrandMatchers");

            migrationBuilder.CreateTable(
                name: "MutedHandles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Token = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MutedHandles", x => x.Id);
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

            migrationBuilder.CreateTable(
                name: "TriageDecisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Gloss = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Token = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Verdict = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriageDecisions", x => x.Id);
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
                name: "IX_TriageDecisions_OwnerId_Source_Token",
                table: "TriageDecisions",
                columns: new[] { "OwnerId", "Source", "Token" },
                unique: true);
        }
    }
}
