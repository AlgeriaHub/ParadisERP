using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Migrations.SqlServer.Migrations
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
                    Id = table.Column<int>(type: "int", nullable: false),
                    Module = table.Column<int>(type: "int", nullable: false),
                    UnitsLeaveMaintenance = table.Column<int>(type: "int", nullable: false),
                    JobsFromReturnNotes = table.Column<int>(type: "int", nullable: false),
                    BookableEmployees = table.Column<int>(type: "int", nullable: false),
                    JobCloser = table.Column<int>(type: "int", nullable: false),
                    CustomerJobs = table.Column<int>(type: "int", nullable: false),
                    CostIncludes = table.Column<int>(type: "int", nullable: false),
                    OverheadPerLabourHour = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    LabourRate = table.Column<int>(type: "int", nullable: false),
                    WorkshopRatePerHour = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    BurdenAndVat = table.Column<int>(type: "int", nullable: false),
                    EmployerBurdenPercent = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ExpenseVatPercent = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    CustomerPrice = table.Column<int>(type: "int", nullable: false),
                    LabourChargePerHour = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    MarkupPercent = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    ReturnRecharge = table.Column<int>(type: "int", nullable: false),
                    PriceBelowCost = table.Column<int>(type: "int", nullable: false),
                    RechargeSharing = table.Column<int>(type: "int", nullable: false),
                    OwnRepairs = table.Column<int>(type: "int", nullable: false),
                    PartsPosting = table.Column<int>(type: "int", nullable: false),
                    LabourInBooks = table.Column<int>(type: "int", nullable: false),
                    CustomerIncome = table.Column<int>(type: "int", nullable: false),
                    Advances = table.Column<int>(type: "int", nullable: false),
                    OpenJobsAtPeriodEnd = table.Column<int>(type: "int", nullable: false),
                    Rework = table.Column<int>(type: "int", nullable: false),
                    RepairHistory = table.Column<int>(type: "int", nullable: false),
                    PostingFromMonth = table.Column<DateOnly>(type: "date", nullable: true),
                    AccountantApproved = table.Column<bool>(type: "bit", nullable: false),
                    ApprovedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
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
