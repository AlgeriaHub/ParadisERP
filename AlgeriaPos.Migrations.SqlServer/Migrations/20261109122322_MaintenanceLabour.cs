using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class MaintenanceLabour : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LaborBookings_EmployeeId_LogRowId",
                table: "LaborBookings");

            migrationBuilder.AlterColumn<Guid>(
                name: "LogRowId",
                table: "LaborBookings",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "LineId",
                table: "LaborBookings",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AlterColumn<Guid>(
                name: "LogRowId",
                table: "LaborBookingChanges",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.AddColumn<Guid>(
                name: "OrderId",
                table: "LaborBookingChanges",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OrderKind",
                table: "LaborBookingChanges",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_LaborBookings_EmployeeId_LogRowId",
                table: "LaborBookings",
                columns: new[] { "EmployeeId", "LogRowId" },
                unique: true,
                filter: "[LogRowId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LaborBookings_OrderKind_OrderId",
                table: "LaborBookings",
                columns: new[] { "OrderKind", "OrderId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LaborBookings_EmployeeId_LogRowId",
                table: "LaborBookings");

            migrationBuilder.DropIndex(
                name: "IX_LaborBookings_OrderKind_OrderId",
                table: "LaborBookings");

            migrationBuilder.DropColumn(
                name: "OrderId",
                table: "LaborBookingChanges");

            migrationBuilder.DropColumn(
                name: "OrderKind",
                table: "LaborBookingChanges");

            migrationBuilder.AlterColumn<Guid>(
                name: "LogRowId",
                table: "LaborBookings",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "LineId",
                table: "LaborBookings",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "LogRowId",
                table: "LaborBookingChanges",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LaborBookings_EmployeeId_LogRowId",
                table: "LaborBookings",
                columns: new[] { "EmployeeId", "LogRowId" },
                unique: true);
        }
    }
}
