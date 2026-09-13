using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIToHuman.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskRiskEnforcement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RiskEnforcedAt",
                table: "tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskEnforcementReason",
                table: "tasks",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskEnforcementStatus",
                table: "tasks",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_Status_RiskRuleVersion",
                table: "tasks",
                columns: new[] { "Status", "RiskRuleVersion" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tasks_Status_RiskRuleVersion",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskEnforcedAt",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskEnforcementReason",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskEnforcementStatus",
                table: "tasks");
        }
    }
}
