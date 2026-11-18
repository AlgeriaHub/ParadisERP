using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class StockMovementCostsAndValuationShadow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "StockMovements",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitCost",
                table: "StockMovements",
                type: "decimal(28,10)",
                precision: 28,
                scale: 10,
                nullable: true);

            // Movements written before kinds existed, classified once from their reason label (P0.4a). The
            // labels are the system's own prefixes; an adjustment typed with the operator's own words cannot
            // be told apart from anything else and stays Unclassified (0). Most specific first.
            migrationBuilder.Sql(@"
UPDATE StockMovements SET Kind = CASE
    WHEN Reason = 'sale' THEN 3
    WHEN Reason = 'refund' THEN 4
    WHEN Reason = 'adjustment' THEN 5
    WHEN Reason LIKE 'receiving%' THEN 2
    WHEN Reason LIKE 'opening%' THEN 1
    WHEN Reason = 'rental-return-transfer' THEN 10
    WHEN Reason = 'rental-return' THEN 9
    WHEN Reason = 'rental-issue' THEN 8
    WHEN Reason LIKE 'transfer-out%' THEN 11
    WHEN Reason LIKE 'transfer-in%' THEN 12
    WHEN Reason LIKE 'transfer-cancel%' THEN 13
    WHEN Reason LIKE 'transfer-lost%' THEN 14
    WHEN Reason = 'mfg-issue' THEN 15
    WHEN Reason = 'mfg-return' THEN 16
    WHEN Reason = 'mfg-output' THEN 17
    WHEN Reason LIKE '%->writtenoff%' THEN 7
    WHEN Reason LIKE '%->%' THEN 6
    ELSE 0 END;");

            migrationBuilder.AddColumn<decimal>(
                name: "UnitCost",
                table: "SaleLines",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StockValuationOpenings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TransferId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TakenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Qty = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockValuationOpenings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockValuationOpenings_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockValuationSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    RefundCost = table.Column<int>(type: "int", nullable: false),
                    UnitCostDecimals = table.Column<int>(type: "int", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockValuationSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_CreatedAtUtc",
                table: "StockMovements",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_StockValuationOpenings_ProductId",
                table: "StockValuationOpenings",
                column: "ProductId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockValuationOpenings");

            migrationBuilder.DropTable(
                name: "StockValuationSettings");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_CreatedAtUtc",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "UnitCost",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "UnitCost",
                table: "SaleLines");
        }
    }
}
