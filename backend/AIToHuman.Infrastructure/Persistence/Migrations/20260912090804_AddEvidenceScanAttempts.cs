using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIToHuman.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEvidenceScanAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastScanAttemptAt",
                table: "evidence",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastScanNote",
                table: "evidence",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScanAttempts",
                table: "evidence",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_evidence_ScanStatus_CreatedAt",
                table: "evidence",
                columns: new[] { "ScanStatus", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_evidence_ScanStatus_CreatedAt",
                table: "evidence");

            migrationBuilder.DropColumn(
                name: "LastScanAttemptAt",
                table: "evidence");

            migrationBuilder.DropColumn(
                name: "LastScanNote",
                table: "evidence");

            migrationBuilder.DropColumn(
                name: "ScanAttempts",
                table: "evidence");
        }
    }
}
