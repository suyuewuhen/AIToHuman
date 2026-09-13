using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIToHuman.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderEscrowAndLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "EscrowAmount",
                table: "orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EscrowHeldAt",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EscrowSettledAt",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EscrowStatus",
                table: "orders",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<string>(
                name: "PaymentReference",
                table: "orders",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RefundedAmount",
                table: "orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ReleasedAmount",
                table: "orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "ledger_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    DebitAccount = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreditAccount = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger_entries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_orders_EscrowStatus_CreatedAt",
                table: "orders",
                columns: new[] { "EscrowStatus", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entries_OccurredAt",
                table: "ledger_entries",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_ledger_entries_OrderId_OccurredAt",
                table: "ledger_entries",
                columns: new[] { "OrderId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ledger_entries");

            migrationBuilder.DropIndex(
                name: "IX_orders_EscrowStatus_CreatedAt",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "EscrowAmount",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "EscrowHeldAt",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "EscrowSettledAt",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "EscrowStatus",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "PaymentReference",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "RefundedAmount",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ReleasedAmount",
                table: "orders");
        }
    }
}
