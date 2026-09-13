using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIToHuman.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRiskAppeal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RiskAppealDecidedAt",
                table: "tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RiskAppealDecidedBy",
                table: "tasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskAppealDecisionNote",
                table: "tasks",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskAppealReason",
                table: "tasks",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskAppealStatus",
                table: "tasks",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RiskAppealedAt",
                table: "tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_tasks_RiskAppealStatus_RiskAppealedAt",
                table: "tasks",
                columns: new[] { "RiskAppealStatus", "RiskAppealedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tasks_RiskAppealStatus_RiskAppealedAt",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskAppealDecidedAt",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskAppealDecidedBy",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskAppealDecisionNote",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskAppealReason",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskAppealStatus",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "RiskAppealedAt",
                table: "tasks");
        }
    }
}
