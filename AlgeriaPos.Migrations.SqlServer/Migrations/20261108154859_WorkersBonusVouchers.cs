using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class WorkersBonusVouchers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PaidByVoucher",
                table: "PayrollRuns",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "PaidByVoucher",
                table: "PayrollRunLines",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "PaymentVoucherBonusLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VoucherId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeNameSnapshot = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EmployeeNumberSnapshot = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    PayrollYear = table.Column<int>(type: "int", nullable: false),
                    PayrollMonth = table.Column<int>(type: "int", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentVoucherBonusLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentVoucherBonusLines_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentVoucherBonusLines_PaymentVouchers_VoucherId",
                        column: x => x.VoucherId,
                        principalTable: "PaymentVouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentVoucherBonusLines_PayrollRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "PayrollRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentVoucherBonusLines_EmployeeId_PayrollYear_PayrollMonth",
                table: "PaymentVoucherBonusLines",
                columns: new[] { "EmployeeId", "PayrollYear", "PayrollMonth" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentVoucherBonusLines_RunId",
                table: "PaymentVoucherBonusLines",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentVoucherBonusLines_VoucherId",
                table: "PaymentVoucherBonusLines",
                column: "VoucherId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentVoucherBonusLines");

            migrationBuilder.DropColumn(
                name: "PaidByVoucher",
                table: "PayrollRuns");

            migrationBuilder.DropColumn(
                name: "PaidByVoucher",
                table: "PayrollRunLines");
        }
    }
}
