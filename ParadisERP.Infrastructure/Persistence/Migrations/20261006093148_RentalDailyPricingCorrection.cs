using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RentalDailyPricingCorrection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RentalDailyPricingCorrections",
                columns: table => new
                {
                    ContractId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BeforeJson = table.Column<string>(type: "TEXT", nullable: true),
                    AfterJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentalDailyPricingCorrections", x => x.ContractId);
                });
            // Only contracts present at upgrade time are eligible; new daily contracts are not.
            migrationBuilder.Sql("INSERT INTO RentalDailyPricingCorrections (ContractId) SELECT Id FROM RentalContracts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Daily rental financial corrections cannot be undone by dropping their audit. Restore a pre-upgrade backup instead.");
        }
    }
}
