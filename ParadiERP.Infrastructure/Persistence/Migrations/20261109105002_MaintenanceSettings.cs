using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MaintenanceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaintenanceSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Module = table.Column<int>(type: "INTEGER", nullable: false),
                    UnitsLeaveMaintenance = table.Column<int>(type: "INTEGER", nullable: false),
                    JobsFromReturnNotes = table.Column<int>(type: "INTEGER", nullable: false),
                    BookableEmployees = table.Column<int>(type: "INTEGER", nullable: false),
                    JobCloser = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomerJobs = table.Column<int>(type: "INTEGER", nullable: false),
                    CostIncludes = table.Column<int>(type: "INTEGER", nullable: false),
                    OverheadPerLabourHour = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    LabourRate = table.Column<int>(type: "INTEGER", nullable: false),
                    WorkshopRatePerHour = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    BurdenAndVat = table.Column<int>(type: "INTEGER", nullable: false),
                    EmployerBurdenPercent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    ExpenseVatPercent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    CustomerPrice = table.Column<int>(type: "INTEGER", nullable: false),
                    LabourChargePerHour = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    MarkupPercent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    ReturnRecharge = table.Column<int>(type: "INTEGER", nullable: false),
                    PriceBelowCost = table.Column<int>(type: "INTEGER", nullable: false),
                    RechargeSharing = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnRepairs = table.Column<int>(type: "INTEGER", nullable: false),
                    PartsPosting = table.Column<int>(type: "INTEGER", nullable: false),
                    LabourInBooks = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomerIncome = table.Column<int>(type: "INTEGER", nullable: false),
                    Advances = table.Column<int>(type: "INTEGER", nullable: false),
                    OpenJobsAtPeriodEnd = table.Column<int>(type: "INTEGER", nullable: false),
                    Rework = table.Column<int>(type: "INTEGER", nullable: false),
                    RepairHistory = table.Column<int>(type: "INTEGER", nullable: false),
                    PostingFromMonth = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    AccountantApproved = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApprovedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaintenanceSettings");
        }
    }
}
