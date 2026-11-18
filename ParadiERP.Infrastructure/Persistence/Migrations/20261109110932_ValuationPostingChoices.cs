using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ValuationPostingChoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CostPriceRevaluation",
                table: "StockValuationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "GapPosting",
                table: "StockValuationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ZeroCostReceipt",
                table: "StockValuationSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CostPriceRevaluation",
                table: "StockValuationSettings");

            migrationBuilder.DropColumn(
                name: "GapPosting",
                table: "StockValuationSettings");

            migrationBuilder.DropColumn(
                name: "ZeroCostReceipt",
                table: "StockValuationSettings");
        }
    }
}
