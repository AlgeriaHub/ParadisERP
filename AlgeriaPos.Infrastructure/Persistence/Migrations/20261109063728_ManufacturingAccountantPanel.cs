using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManufacturingAccountantPanel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AccountantApproved",
                table: "ManufacturingCostingSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovedAtUtc",
                table: "ManufacturingCostingSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                table: "ManufacturingCostingSettings",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExpenseBasis",
                table: "ManufacturingCostingSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "ExpenseVatPercent",
                table: "ManufacturingCostingSettings",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateOnly>(
                name: "PostingFromMonth",
                table: "ManufacturingCostingSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScrapRecovery",
                table: "ManufacturingCostingSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "ScrapRecoveryPercent",
                table: "ManufacturingCostingSettings",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "StockOperations",
                table: "ManufacturingCostingSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ManufacturingMonthPostings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    Month = table.Column<int>(type: "INTEGER", nullable: false),
                    Generation = table.Column<int>(type: "INTEGER", nullable: false),
                    Entries = table.Column<int>(type: "INTEGER", nullable: false),
                    Indexes = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalDebit = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    PostedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PostedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ReversedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReversedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManufacturingMonthPostings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingMonthPostings_Year_Month_Generation",
                table: "ManufacturingMonthPostings",
                columns: new[] { "Year", "Month", "Generation" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManufacturingMonthPostings");

            migrationBuilder.DropColumn(
                name: "AccountantApproved",
                table: "ManufacturingCostingSettings");

            migrationBuilder.DropColumn(
                name: "ApprovedAtUtc",
                table: "ManufacturingCostingSettings");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                table: "ManufacturingCostingSettings");

            migrationBuilder.DropColumn(
                name: "ExpenseBasis",
                table: "ManufacturingCostingSettings");

            migrationBuilder.DropColumn(
                name: "ExpenseVatPercent",
                table: "ManufacturingCostingSettings");

            migrationBuilder.DropColumn(
                name: "PostingFromMonth",
                table: "ManufacturingCostingSettings");

            migrationBuilder.DropColumn(
                name: "ScrapRecovery",
                table: "ManufacturingCostingSettings");

            migrationBuilder.DropColumn(
                name: "ScrapRecoveryPercent",
                table: "ManufacturingCostingSettings");

            migrationBuilder.DropColumn(
                name: "StockOperations",
                table: "ManufacturingCostingSettings");
        }
    }
}
