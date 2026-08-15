using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddInputEventCodeSet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CodeSet",
                table: "InputEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "windows-vk-v1");

            // ADR-012/035: every row that predates CodeSet is truthful raw Windows VK data.
            // PostgreSQL can add a constant-default column without rewriting the historical
            // table. Drop that temporary default immediately so new writes must stay explicit.
            migrationBuilder.AlterColumn<string>(
                name: "CodeSet",
                table: "InputEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldDefaultValue: "windows-vk-v1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CodeSet",
                table: "InputEvents");
        }
    }
}
