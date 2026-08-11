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
                nullable: true);

            // ADR-012/035: every row that predates CodeSet is truthful raw Windows VK data.
            // Tag it in place without rewriting Code, then make the new strict field required.
            migrationBuilder.Sql("""
                UPDATE "InputEvents"
                SET "CodeSet" = 'windows-vk-v1'
                WHERE "CodeSet" IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "CodeSet",
                table: "InputEvents",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);
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
