using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIToHuman.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskDraftRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "task_draft_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    District = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RewardAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RewardCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    AcceptanceCriteriaJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    ExecutionAddress = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ApplicationDeadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RiskVerdict = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RiskRuleCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    RiskRuleVersion = table.Column<int>(type: "integer", nullable: false),
                    EditedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeSummary = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_task_draft_revisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_task_draft_revisions_TaskId_Revision",
                table: "task_draft_revisions",
                columns: new[] { "TaskId", "Revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_draft_revisions");
        }
    }
}
