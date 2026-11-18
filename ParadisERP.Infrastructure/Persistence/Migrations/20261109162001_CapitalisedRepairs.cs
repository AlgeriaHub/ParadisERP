using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CapitalisedRepairs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CapitaliseAboveAmount",
                table: "MaintenanceSettings",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "PerPieceAboveValue",
                table: "MaintenanceSettings",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "CapitalisedAmount",
                table: "MaintenanceJobs",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CapitalisedAtUtc",
                table: "MaintenanceJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CapitalisedMovementId",
                table: "MaintenanceJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MaintenanceJobCapitalisedLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Element = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Slot = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceJobCapitalisedLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceJobCapitalisedLines_MaintenanceJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "MaintenanceJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobs_CapitalisedAtUtc",
                table: "MaintenanceJobs",
                column: "CapitalisedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobCapitalisedLines_JobId",
                table: "MaintenanceJobCapitalisedLines",
                column: "JobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaintenanceJobCapitalisedLines");

            migrationBuilder.DropIndex(
                name: "IX_MaintenanceJobs_CapitalisedAtUtc",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "CapitaliseAboveAmount",
                table: "MaintenanceSettings");

            migrationBuilder.DropColumn(
                name: "PerPieceAboveValue",
                table: "MaintenanceSettings");

            migrationBuilder.DropColumn(
                name: "CapitalisedAmount",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "CapitalisedAtUtc",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "CapitalisedMovementId",
                table: "MaintenanceJobs");
        }
    }
}
