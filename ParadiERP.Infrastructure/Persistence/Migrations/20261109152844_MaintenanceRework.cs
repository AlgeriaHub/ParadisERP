using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MaintenanceRework : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "GuaranteeMonths",
                table: "MaintenanceSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3); // MS-27 (Phase M7c): a row saved before keeps the default guarantee of three months.

            migrationBuilder.AddColumn<decimal>(
                name: "ProvisionPercent",
                table: "MaintenanceSettings",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateOnly>(
                name: "GuaranteeUntil",
                table: "MaintenanceJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ProvisionAmount",
                table: "MaintenanceJobs",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ProvisionPercent",
                table: "MaintenanceJobs",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReworkOfJobId",
                table: "MaintenanceJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobs_ReworkOfJobId",
                table: "MaintenanceJobs",
                column: "ReworkOfJobId");

            migrationBuilder.AddForeignKey(
                name: "FK_MaintenanceJobs_MaintenanceJobs_ReworkOfJobId",
                table: "MaintenanceJobs",
                column: "ReworkOfJobId",
                principalTable: "MaintenanceJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MaintenanceJobs_MaintenanceJobs_ReworkOfJobId",
                table: "MaintenanceJobs");

            migrationBuilder.DropIndex(
                name: "IX_MaintenanceJobs_ReworkOfJobId",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "GuaranteeMonths",
                table: "MaintenanceSettings");

            migrationBuilder.DropColumn(
                name: "ProvisionPercent",
                table: "MaintenanceSettings");

            migrationBuilder.DropColumn(
                name: "GuaranteeUntil",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "ProvisionAmount",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "ProvisionPercent",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "ReworkOfJobId",
                table: "MaintenanceJobs");
        }
    }
}
