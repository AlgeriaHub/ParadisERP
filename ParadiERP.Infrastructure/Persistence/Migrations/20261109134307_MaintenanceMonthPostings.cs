using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MaintenanceMonthPostings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaintenanceMonthPostings",
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
                    table.PrimaryKey("PK_MaintenanceMonthPostings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceMonthPostings_Year_Month_Generation",
                table: "MaintenanceMonthPostings",
                columns: new[] { "Year", "Month", "Generation" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaintenanceMonthPostings");
        }
    }
}
