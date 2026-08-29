using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class RecapWindowIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Recaps_OwnerId_WindowStart",
                table: "Recaps");

            migrationBuilder.AddColumn<string>(
                name: "LocalDate",
                table: "Recaps",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "Recaps",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WindowEndExclusive",
                table: "Recaps",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WindowKey",
                table: "Recaps",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WindowKind",
                table: "Recaps",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WindowVersion",
                table: "Recaps",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Recaps_OwnerId_WindowKey",
                table: "Recaps",
                columns: new[] { "OwnerId", "WindowKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Recaps_OwnerId_WindowKey",
                table: "Recaps");

            migrationBuilder.DropColumn(
                name: "LocalDate",
                table: "Recaps");

            migrationBuilder.DropColumn(
                name: "TimeZone",
                table: "Recaps");

            migrationBuilder.DropColumn(
                name: "WindowEndExclusive",
                table: "Recaps");

            migrationBuilder.DropColumn(
                name: "WindowKey",
                table: "Recaps");

            migrationBuilder.DropColumn(
                name: "WindowKind",
                table: "Recaps");

            migrationBuilder.DropColumn(
                name: "WindowVersion",
                table: "Recaps");

            migrationBuilder.CreateIndex(
                name: "IX_Recaps_OwnerId_WindowStart",
                table: "Recaps",
                columns: new[] { "OwnerId", "WindowStart" },
                unique: true);
        }
    }
}
