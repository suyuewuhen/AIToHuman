using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIToHuman.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRiskRuleCatalogRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "risk_rule_catalog_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CatalogJson = table.Column<string>(type: "text", nullable: false),
                    ChangeSummary = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    ChangeReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_risk_rule_catalog_revisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_risk_rule_catalog_revisions_CreatedAt",
                table: "risk_rule_catalog_revisions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_risk_rule_catalog_revisions_Version",
                table: "risk_rule_catalog_revisions",
                column: "Version",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "risk_rule_catalog_revisions");
        }
    }
}
