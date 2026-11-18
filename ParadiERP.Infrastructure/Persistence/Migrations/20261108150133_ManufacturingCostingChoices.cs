using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManufacturingCostingChoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManufacturingAccountMappings",
                columns: table => new
                {
                    Slot = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LedgerAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManufacturingAccountMappings", x => x.Slot);
                    table.ForeignKey(
                        name: "FK_ManufacturingAccountMappings_LedgerAccounts_LedgerAccountId",
                        column: x => x.LedgerAccountId,
                        principalTable: "LedgerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ManufacturingCostingSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Model = table.Column<int>(type: "INTEGER", nullable: false),
                    OverheadPerLabourHour = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    ReleaseRule = table.Column<int>(type: "INTEGER", nullable: false),
                    EmployerBurdenUse = table.Column<int>(type: "INTEGER", nullable: false),
                    EmployerBurdenPercent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    MonthEndWorkInProgress = table.Column<int>(type: "INTEGER", nullable: false),
                    LateCosts = table.Column<int>(type: "INTEGER", nullable: false),
                    Variances = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManufacturingCostingSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingAccountMappings_LedgerAccountId",
                table: "ManufacturingAccountMappings",
                column: "LedgerAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManufacturingAccountMappings");

            migrationBuilder.DropTable(
                name: "ManufacturingCostingSettings");
        }
    }
}
