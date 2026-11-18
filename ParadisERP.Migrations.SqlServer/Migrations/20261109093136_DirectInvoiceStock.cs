using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Migrations.SqlServer.Migrations
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
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "DirectInvoiceStockChangedAtUtc",
                table: "StockValuationSettings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DirectInvoiceStockChangedBy",
                table: "StockValuationSettings",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "StockCostBasis",
                table: "InvoiceLines",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StockUnitCost",
                table: "InvoiceLines",
                type: "decimal(28,10)",
                precision: 28,
                scale: 10,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StockReturnedQty",
                table: "CreditNoteLines",
                type: "decimal(18,3)",
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
