using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DirectInvoiceStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DirectInvoiceStock",
                table: "StockValuationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DirectInvoiceStockChangedAtUtc",
                table: "StockValuationSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DirectInvoiceStockChangedBy",
                table: "StockValuationSettings",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StockCostBasis",
                table: "InvoiceLines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StockUnitCost",
                table: "InvoiceLines",
                type: "TEXT",
                precision: 28,
                scale: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StockReturnedQty",
                table: "CreditNoteLines",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DirectInvoiceStock",
                table: "StockValuationSettings");

            migrationBuilder.DropColumn(
                name: "DirectInvoiceStockChangedAtUtc",
                table: "StockValuationSettings");

            migrationBuilder.DropColumn(
                name: "DirectInvoiceStockChangedBy",
                table: "StockValuationSettings");

            migrationBuilder.DropColumn(
                name: "StockCostBasis",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "StockUnitCost",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "StockReturnedQty",
                table: "CreditNoteLines");
        }
    }
}
