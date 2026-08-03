using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Heartbeat.Server.Migrations
{
    /// <summary>
    /// ADR-031 issue 02：Episode 与 RecurrenceProbe。
    /// - Episodes：用户确认的有界事实，UUIDv7 主键、本地叙事日、近似起止时间、
    ///   可空单一 RelatedStrandId（Restrict——Strand 无删除操作，解除关联是显式领域写）。
    /// - RecurrenceProbes：附在 Episode 上的复现探针，与 Matcher 同 canonical 谓词；
    ///   (EpisodeId, Source, StepsJson) 唯一（含已解决行——解决结果钉住谓词，不再重复发问）。
    /// 纯增表，不动存量数据。
    /// </summary>
    public partial class EpisodeRecurrenceProbe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Episodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    LocalDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    ApproximateStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApproximateEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RelatedStrandId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Episodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Episodes_Strands_RelatedStrandId",
                        column: x => x.RelatedStrandId,
                        principalTable: "Strands",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecurrenceProbes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    EpisodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StepsJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurrenceProbes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurrenceProbes_Episodes_EpisodeId",
                        column: x => x.EpisodeId,
                        principalTable: "Episodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_OwnerId_LocalDate",
                table: "Episodes",
                columns: new[] { "OwnerId", "LocalDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Episodes_RelatedStrandId",
                table: "Episodes",
                column: "RelatedStrandId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurrenceProbes_EpisodeId_Source_StepsJson",
                table: "RecurrenceProbes",
                columns: new[] { "EpisodeId", "Source", "StepsJson" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecurrenceProbes_OwnerId_Status",
                table: "RecurrenceProbes",
                columns: new[] { "OwnerId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecurrenceProbes");

            migrationBuilder.DropTable(
                name: "Episodes");
        }
    }
}
