using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class AskingWindowIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DailyQuestionSets_OwnerId_WindowStart",
                table: "DailyQuestionSets");

            migrationBuilder.AddColumn<string>(
                name: "LocalDate",
                table: "DailyQuestionSets",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZone",
                table: "DailyQuestionSets",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WindowEndExclusive",
                table: "DailyQuestionSets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WindowKey",
                table: "DailyQuestionSets",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WindowKind",
                table: "DailyQuestionSets",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WindowVersion",
                table: "DailyQuestionSets",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailyQuestionSets_OwnerId_WindowKey",
                table: "DailyQuestionSets",
                columns: new[] { "OwnerId", "WindowKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DailyQuestionSets_OwnerId_WindowKey",
                table: "DailyQuestionSets");

            migrationBuilder.DropColumn(
                name: "LocalDate",
                table: "DailyQuestionSets");

            migrationBuilder.DropColumn(
                name: "TimeZone",
                table: "DailyQuestionSets");

            migrationBuilder.DropColumn(
                name: "WindowEndExclusive",
                table: "DailyQuestionSets");

            migrationBuilder.DropColumn(
                name: "WindowKey",
                table: "DailyQuestionSets");

            migrationBuilder.DropColumn(
                name: "WindowKind",
                table: "DailyQuestionSets");

            migrationBuilder.DropColumn(
                name: "WindowVersion",
                table: "DailyQuestionSets");

            migrationBuilder.CreateIndex(
                name: "IX_DailyQuestionSets_OwnerId_WindowStart",
                table: "DailyQuestionSets",
                columns: new[] { "OwnerId", "WindowStart" },
                unique: true);
        }
    }
}
