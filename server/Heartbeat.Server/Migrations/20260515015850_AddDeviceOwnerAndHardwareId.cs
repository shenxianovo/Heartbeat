using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceOwnerAndHardwareId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Devices_DeviceName",
                table: "Devices");

            migrationBuilder.AddColumn<string>(
                name: "HardwareId",
                table: "Devices",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "OwnerId",
                table: "Devices",
                type: "text",
                nullable: false,
                defaultValue: "");

            // Backfill existing devices with real MachineGuid values
            migrationBuilder.Sql("""
                UPDATE "Devices"
                SET "OwnerId" = '019d9026-def4-74db-bf9a-f854c16a993e',
                    "HardwareId" = 'd543153b-f4f1-4c51-9f84-edd978401935'
                WHERE "DeviceName" = 'PC-工位';

                UPDATE "Devices"
                SET "OwnerId" = '019d9026-def4-74db-bf9a-f854c16a993e',
                    "HardwareId" = '0b2ab3f5-f79c-4bbe-997f-0a1fbe1f33b6'
                WHERE "DeviceName" = 'PC';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_OwnerId_HardwareId",
                table: "Devices",
                columns: new[] { "OwnerId", "HardwareId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Devices_OwnerId_HardwareId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "HardwareId",
                table: "Devices");

            migrationBuilder.DropColumn(
                name: "OwnerId",
                table: "Devices");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_DeviceName",
                table: "Devices",
                column: "DeviceName",
                unique: true);
        }
    }
}
