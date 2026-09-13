using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIToHuman.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskRiskAssessment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RiskAssessedAt",
                table: "tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskCategory",
                table: "tasks",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskReviewNote",
                table: "tasks",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskReviewStatus",
                table: "tasks",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "NotRequired");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RiskReviewedAt",
                table: "tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RiskReviewedBy",
                table: "tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskRuleCode",
                table: "tasks",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RiskRuleVersion",
                table: "tasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RiskSummary",
                table: "tasks",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskVerdict",
                table: "tasks",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Allowed");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_RiskReviewStatus_CreatedAt",
                table: "tasks",
                columns: new[] { "RiskReviewStatus", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tasks_RiskReviewStatus_CreatedAt",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskAssessedAt",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskCategory",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskReviewNote",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskReviewStatus",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskReviewedAt",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskReviewedBy",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskRuleCode",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskRuleVersion",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskSummary",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskVerdict",
                table: "tasks");
        }
    }
}
