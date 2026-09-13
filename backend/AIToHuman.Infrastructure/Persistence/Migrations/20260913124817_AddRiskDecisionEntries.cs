using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIToHuman.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRiskDecisionEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "risk_decision_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Verdict = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RuleCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Category = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    RuleVersion = table.Column<int>(type: "integer", nullable: false),
                    RewardAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_risk_decision_entries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_risk_decision_entries_OccurredAt_RuleCode",
                table: "risk_decision_entries",
                columns: new[] { "OccurredAt", "RuleCode" });

            migrationBuilder.CreateIndex(
                name: "IX_risk_decision_entries_TaskId_OccurredAt",
                table: "risk_decision_entries",
                columns: new[] { "TaskId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "risk_decision_entries");
        }
    }
}
