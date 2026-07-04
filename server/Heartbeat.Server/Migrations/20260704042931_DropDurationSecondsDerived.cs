using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <inheritdoc />
    public partial class DropDurationSecondsDerived : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DurationSeconds",
                table: "ActivitySegments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DurationSeconds",
                table: "ActivitySegments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // 时长是派生量(ADR-018):回滚时从区间重算,不会真丢数据。
            migrationBuilder.Sql(@"
                UPDATE ""ActivitySegments""
                SET ""DurationSeconds"" = CAST(EXTRACT(EPOCH FROM (""EndTime"" - ""StartTime"")) AS integer);
            ");
        }
    }
}
