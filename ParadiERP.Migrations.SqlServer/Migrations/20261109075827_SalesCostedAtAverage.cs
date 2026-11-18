using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Migrations.SqlServer.Migrations
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
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "SalesCostChangedAtUtc",
                table: "StockValuationSettings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SalesCostChangedBy",
                table: "StockValuationSettings",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitCost",
                table: "SaleLines",
                type: "decimal(28,10)",
                precision: 28,
                scale: 10,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,3)",
                oldPrecision: 18,
                oldScale: 3,
                oldNullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CostBasis",
                table: "SaleLines",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "StockValuationSwitches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Method = table.Column<int>(type: "int", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    By = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    RefundCost = table.Column<int>(type: "int", nullable: false),
                    UnitCostDecimals = table.Column<int>(type: "int", nullable: false),
                    ShadowValue = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    BookValue = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Adjustment = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    LedgerEntryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
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

            migrationBuilder.AlterColumn<decimal>(
                name: "UnitCost",
                table: "SaleLines",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(28,10)",
                oldPrecision: 28,
                oldScale: 10,
                oldNullable: true);
        }
    }
}
