using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MaintenanceCustomerJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ProductId",
                table: "MaintenanceJobs",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AddColumn<DateTime>(
                name: "BelowCostConfirmedAtUtc",
                table: "MaintenanceJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BelowCostConfirmedBy",
                table: "MaintenanceJobs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CostAtPricing",
                table: "MaintenanceJobs",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "MaintenanceJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Equipment",
                table: "MaintenanceJobs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InvoiceId",
                table: "MaintenanceJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InvoicedAtUtc",
                table: "MaintenanceJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoicedBy",
                table: "MaintenanceJobs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Price",
                table: "MaintenanceJobs",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PriceBasis",
                table: "MaintenanceJobs",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PricedAtUtc",
                table: "MaintenanceJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PricedBy",
                table: "MaintenanceJobs",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PricedByUserId",
                table: "MaintenanceJobs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ProposedPrice",
                table: "MaintenanceJobs",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: true);

            // Every job so far is internal or recharged and is never invoiced; untaxed (1) is the rule for every new bill.
            migrationBuilder.AddColumn<int>(
                name: "TaxMode",
                table: "MaintenanceJobs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "MaintenanceRepairRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RatePerUnit = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceRepairRates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceRepairRates_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobs_CustomerId",
                table: "MaintenanceJobs",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobs_InvoiceId",
                table: "MaintenanceJobs",
                column: "InvoiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceRepairRates_ProductId",
                table: "MaintenanceRepairRates",
                column: "ProductId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_MaintenanceJobs_Customers_CustomerId",
                table: "MaintenanceJobs",
                column: "CustomerId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MaintenanceJobs_Invoices_InvoiceId",
                table: "MaintenanceJobs",
                column: "InvoiceId",
                principalTable: "Invoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MaintenanceJobs_Customers_CustomerId",
                table: "MaintenanceJobs");

            migrationBuilder.DropForeignKey(
                name: "FK_MaintenanceJobs_Invoices_InvoiceId",
                table: "MaintenanceJobs");

            migrationBuilder.DropTable(
                name: "MaintenanceRepairRates");

            migrationBuilder.DropIndex(
                name: "IX_MaintenanceJobs_CustomerId",
                table: "MaintenanceJobs");

            migrationBuilder.DropIndex(
                name: "IX_MaintenanceJobs_InvoiceId",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "BelowCostConfirmedAtUtc",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "BelowCostConfirmedBy",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "CostAtPricing",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "CustomerId",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "Equipment",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "InvoiceId",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "InvoicedAtUtc",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "InvoicedBy",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "Price",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "PriceBasis",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "PricedAtUtc",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "PricedBy",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "PricedByUserId",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "ProposedPrice",
                table: "MaintenanceJobs");

            migrationBuilder.DropColumn(
                name: "TaxMode",
                table: "MaintenanceJobs");

            migrationBuilder.AlterColumn<Guid>(
                name: "ProductId",
                table: "MaintenanceJobs",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
