using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class ManufacturingSaleAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManufacturingSaleExceptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SaleLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sku = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DeviceCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Reference = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    SoldAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CashierUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CashierName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PushedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Role = table.Column<int>(type: "int", nullable: false),
                    WithdrawnAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SoldBeforeWithdrawal = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManufacturingSaleExceptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManufacturingSaleExceptions_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ManufacturingSaleExceptions_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ManufacturingSaleExceptions_Sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "Sales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductSaleStatusChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ChangedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    OldRole = table.Column<int>(type: "int", nullable: false),
                    NewRole = table.Column<int>(type: "int", nullable: false),
                    OldAlsoForSale = table.Column<bool>(type: "bit", nullable: false),
                    NewAlsoForSale = table.Column<bool>(type: "bit", nullable: false),
                    OldForSale = table.Column<bool>(type: "bit", nullable: false),
                    NewForSale = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductSaleStatusChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductSaleStatusChanges_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingSaleExceptions_BranchId_SoldAtUtc",
                table: "ManufacturingSaleExceptions",
                columns: new[] { "BranchId", "SoldAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingSaleExceptions_ProductId_SoldAtUtc",
                table: "ManufacturingSaleExceptions",
                columns: new[] { "ProductId", "SoldAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingSaleExceptions_SaleId",
                table: "ManufacturingSaleExceptions",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingSaleExceptions_SaleLineId",
                table: "ManufacturingSaleExceptions",
                column: "SaleLineId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductSaleStatusChanges_ProductId_ChangedAtUtc",
                table: "ProductSaleStatusChanges",
                columns: new[] { "ProductId", "ChangedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManufacturingSaleExceptions");

            migrationBuilder.DropTable(
                name: "ProductSaleStatusChanges");
        }
    }
}
