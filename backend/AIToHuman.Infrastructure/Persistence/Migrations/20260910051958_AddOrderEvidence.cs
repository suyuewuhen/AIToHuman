using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIToHuman.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EvidenceNote",
                table: "orders",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewNote",
                table: "orders",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewedAt",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SubmittedAt",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EvidenceNote",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ReviewNote",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "SubmittedAt",
                table: "orders");
        }
    }
}
