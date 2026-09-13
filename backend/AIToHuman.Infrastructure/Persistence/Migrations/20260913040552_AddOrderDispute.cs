using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIToHuman.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderDispute : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DisputeOpenedAt",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DisputeOpenedBy",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisputeReason",
                table: "orders",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisputeResolution",
                table: "orders",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisputeResolutionNote",
                table: "orders",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DisputeResolvedAt",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_Status_CreatedAt",
                table: "orders",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_orders_Status_CreatedAt",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "DisputeOpenedAt",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "DisputeOpenedBy",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "DisputeReason",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "DisputeResolution",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "DisputeResolutionNote",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "DisputeResolvedAt",
                table: "orders");
        }
    }
}
