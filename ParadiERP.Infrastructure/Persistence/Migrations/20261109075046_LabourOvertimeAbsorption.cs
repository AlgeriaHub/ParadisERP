using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LabourOvertimeAbsorption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LabourAbsorption",
                table: "ManufacturingCostingSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "OvertimeHours",
                table: "LaborBookings",
                type: "TEXT",
                precision: 9,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "OvertimeHours",
                table: "LaborBookingChanges",
                type: "TEXT",
                precision: 9,
                scale: 2,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LabourAbsorption",
                table: "ManufacturingCostingSettings");

            migrationBuilder.DropColumn(
                name: "OvertimeHours",
                table: "LaborBookings");

            migrationBuilder.DropColumn(
                name: "OvertimeHours",
                table: "LaborBookingChanges");
        }
    }
}
