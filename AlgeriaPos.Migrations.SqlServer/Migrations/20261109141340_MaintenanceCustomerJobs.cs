using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class MaintenanceCustomerJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQL Server will not alter a column an index or a foreign key depends on: both indexes over ProductId and its
            // foreign key are dropped around the change and recreated exactly as they were. Every row keeps its product.
            migrationBuilder.DropForeignKey(name: "FK_MaintenanceJobs_Products_ProductId", table: "MaintenanceJobs");
            migrationBuilder.DropIndex(name: "IX_MaintenanceJobs_BranchId_ProductId_Status", table: "MaintenanceJobs");
            migrationBuilder.DropIndex(name: "IX_MaintenanceJobs_ProductId", table: "MaintenanceJobs");

            migrationBuilder.AlterColumn<Guid>(
                name: "ProductId",
                table: "MaintenanceJobs",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.CreateIndex(name: "IX_MaintenanceJobs_BranchId_ProductId_Status", table: "MaintenanceJobs", columns: new[] { "BranchId", "ProductId", "Status" });
            migrationBuilder.CreateIndex(name: "IX_MaintenanceJobs_ProductId", table: "MaintenanceJobs", column: "ProductId");
            migrationBuilder.AddForeignKey(name: "FK_MaintenanceJobs_Products_ProductId", table: "MaintenanceJobs", column: "ProductId",
                principalTable: "Products", principalColumn: "Id", onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddColumn<DateTime>(
                name: "BelowCostConfirmedAtUtc",
                table: "MaintenanceJobs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BelowCostConfirmedBy",
                table: "MaintenanceJobs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CostAtPricing",
                table: "MaintenanceJobs",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CustomerId",
                table: "MaintenanceJobs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Equipment",
                table: "MaintenanceJobs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InvoiceId",
                table: "MaintenanceJobs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InvoicedAtUtc",
                table: "MaintenanceJobs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InvoicedBy",
                table: "MaintenanceJobs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Price",
                table: "MaintenanceJobs",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PriceBasis",
                table: "MaintenanceJobs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PricedAtUtc",
                table: "MaintenanceJobs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PricedBy",
                table: "MaintenanceJobs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PricedByUserId",
                table: "MaintenanceJobs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ProposedPrice",
                table: "MaintenanceJobs",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            // Every job so far is internal or recharged and is never invoiced; untaxed (1) is the rule for every new bill.
            migrationBuilder.AddColumn<int>(
                name: "TaxMode",
                table: "MaintenanceJobs",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "MaintenanceRepairRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RatePerUnit = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                unique: true,
                filter: "[InvoiceId] IS NOT NULL");

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

            // SQL Server will not alter a column an index or a foreign key depends on: both indexes over ProductId and its
            // foreign key are dropped around the change and recreated exactly as they were. A customer job with no product cannot go back.
            migrationBuilder.DropForeignKey(name: "FK_MaintenanceJobs_Products_ProductId", table: "MaintenanceJobs");
            migrationBuilder.DropIndex(name: "IX_MaintenanceJobs_BranchId_ProductId_Status", table: "MaintenanceJobs");
            migrationBuilder.DropIndex(name: "IX_MaintenanceJobs_ProductId", table: "MaintenanceJobs");

            migrationBuilder.AlterColumn<Guid>(
                name: "ProductId",
                table: "MaintenanceJobs",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(name: "IX_MaintenanceJobs_BranchId_ProductId_Status", table: "MaintenanceJobs", columns: new[] { "BranchId", "ProductId", "Status" });
            migrationBuilder.CreateIndex(name: "IX_MaintenanceJobs_ProductId", table: "MaintenanceJobs", column: "ProductId");
            migrationBuilder.AddForeignKey(name: "FK_MaintenanceJobs_Products_ProductId", table: "MaintenanceJobs", column: "ProductId",
                principalTable: "Products", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        }
    }
}
