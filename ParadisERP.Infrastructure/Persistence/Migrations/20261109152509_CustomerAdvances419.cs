using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomerAdvances419 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AdvanceTaxAmount",
                table: "ReceiptVouchers",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AdvanceTaxRate",
                table: "ReceiptVouchers",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "MaintenanceJobId",
                table: "ReceiptVouchers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustomerAdvanceApplications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReceiptVoucherId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReceiptAllocationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    AppliedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerAdvanceApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerAdvanceApplications_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerAdvanceApplications_ReceiptVouchers_ReceiptVoucherId",
                        column: x => x.ReceiptVoucherId,
                        principalTable: "ReceiptVouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceivableSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    AdvancesHeldOn = table.Column<int>(type: "INTEGER", nullable: false),
                    AdvanceVat = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceivableSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptVouchers_MaintenanceJobId",
                table: "ReceiptVouchers",
                column: "MaintenanceJobId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAdvanceApplications_InvoiceId",
                table: "CustomerAdvanceApplications",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAdvanceApplications_ReceiptVoucherId",
                table: "CustomerAdvanceApplications",
                column: "ReceiptVoucherId");

            migrationBuilder.AddForeignKey(
                name: "FK_ReceiptVouchers_MaintenanceJobs_MaintenanceJobId",
                table: "ReceiptVouchers",
                column: "MaintenanceJobId",
                principalTable: "MaintenanceJobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ReceiptVouchers_MaintenanceJobs_MaintenanceJobId",
                table: "ReceiptVouchers");

            migrationBuilder.DropTable(
                name: "CustomerAdvanceApplications");

            migrationBuilder.DropTable(
                name: "ReceivableSettings");

            migrationBuilder.DropIndex(
                name: "IX_ReceiptVouchers_MaintenanceJobId",
                table: "ReceiptVouchers");

            migrationBuilder.DropColumn(
                name: "AdvanceTaxAmount",
                table: "ReceiptVouchers");

            migrationBuilder.DropColumn(
                name: "AdvanceTaxRate",
                table: "ReceiptVouchers");

            migrationBuilder.DropColumn(
                name: "MaintenanceJobId",
                table: "ReceiptVouchers");
        }
    }
}
