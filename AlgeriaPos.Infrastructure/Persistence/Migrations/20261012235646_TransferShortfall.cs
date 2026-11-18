using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TransferShortfall : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A shortfall reported at receipt and how it was answered (owner, 2026-10-13). Nothing was ever
            // reported short on an existing line, so 0 is every row's true value.
            migrationBuilder.AddColumn<decimal>(
                name: "QtyShortConfirmed",
                table: "StockTransferLines",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "QtyShortPending",
                table: "StockTransferLines",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "QtyShortRejected",
                table: "StockTransferLines",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "QtyShortReported",
                table: "StockTransferLines",
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
                name: "QtyShortConfirmed",
                table: "StockTransferLines");

            migrationBuilder.DropColumn(
                name: "QtyShortPending",
                table: "StockTransferLines");

            migrationBuilder.DropColumn(
                name: "QtyShortRejected",
                table: "StockTransferLines");

            migrationBuilder.DropColumn(
                name: "QtyShortReported",
                table: "StockTransferLines");
        }
    }
}
