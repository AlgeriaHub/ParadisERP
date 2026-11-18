using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContractClosesWhenPaid : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClosedShortBy",
                table: "RentalContracts",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosedShortReason",
                table: "RentalContracts",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "GoodsBackAtUtc",
                table: "RentalContracts",
                type: "TEXT",
                nullable: true);

            // A contract closed before 2026-10-02 closed the moment its goods were back, so that
            // moment is its close date. Nothing else changes for it.
            migrationBuilder.Sql("UPDATE RentalContracts SET GoodsBackAtUtc = ClosedAtUtc WHERE ClosedAtUtc IS NOT NULL AND GoodsBackAtUtc IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClosedShortBy",
                table: "RentalContracts");

            migrationBuilder.DropColumn(
                name: "ClosedShortReason",
                table: "RentalContracts");

            migrationBuilder.DropColumn(
                name: "GoodsBackAtUtc",
                table: "RentalContracts");
        }
    }
}
