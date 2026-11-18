using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesCostedAtAverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SalesCost",
                table: "StockValuationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "SalesCostChangedAtUtc",
                table: "StockValuationSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SalesCostChangedBy",
                table: "StockValuationSettings",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CostBasis",
                table: "SaleLines",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "StockValuationSwitches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Method = table.Column<int>(type: "INTEGER", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    By = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false),
                    RefundCost = table.Column<int>(type: "INTEGER", nullable: false),
                    UnitCostDecimals = table.Column<int>(type: "INTEGER", nullable: false),
                    ShadowValue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    BookValue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    Adjustment = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    LedgerEntryId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockValuationSwitches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockValuationSwitches_AtUtc",
                table: "StockValuationSwitches",
                column: "AtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockValuationSwitches");

            migrationBuilder.DropColumn(
                name: "SalesCost",
                table: "StockValuationSettings");

            migrationBuilder.DropColumn(
                name: "SalesCostChangedAtUtc",
                table: "StockValuationSettings");

            migrationBuilder.DropColumn(
                name: "SalesCostChangedBy",
                table: "StockValuationSettings");

            migrationBuilder.DropColumn(
                name: "CostBasis",
                table: "SaleLines");
        }
    }
}
