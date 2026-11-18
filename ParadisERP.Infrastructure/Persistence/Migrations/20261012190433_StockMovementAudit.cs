using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StockMovementAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DocumentNumber",
                table: "StockMovements",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MovedQty",
                table: "StockMovements",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupplierId",
                table: "StockMovements",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "StockMovements",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserName",
                table: "StockMovements",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_ProductId_CreatedAtUtc",
                table: "StockMovements",
                columns: new[] { "ProductId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockMovements_ProductId_CreatedAtUtc",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "DocumentNumber",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "MovedQty",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "UserName",
                table: "StockMovements");
        }
    }
}
