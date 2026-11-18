using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Suppliers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SupplierId",
                table: "PaymentVouchers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SupplierId",
                table: "LedgerEntryLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Suppliers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Address = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    FiscalIds = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    Balance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    OpeningBalance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    OpeningBalanceOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    OpeningBalanceSetAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    OpeningBalanceSetBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SupplierOpeningCorrections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SupplierId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    EffectiveOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierOpeningCorrections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierOpeningCorrections_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierOpeningCorrections_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentVouchers_SupplierId",
                table: "PaymentVouchers",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntryLines_AccountId_SupplierId",
                table: "LedgerEntryLines",
                columns: new[] { "AccountId", "SupplierId" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierOpeningCorrections_BranchId",
                table: "SupplierOpeningCorrections",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierOpeningCorrections_Number",
                table: "SupplierOpeningCorrections",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierOpeningCorrections_SupplierId_CreatedAtUtc",
                table: "SupplierOpeningCorrections",
                columns: new[] { "SupplierId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_Code",
                table: "Suppliers",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_Name",
                table: "Suppliers",
                column: "Name");

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentVouchers_Suppliers_SupplierId",
                table: "PaymentVouchers",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PaymentVouchers_Suppliers_SupplierId",
                table: "PaymentVouchers");

            migrationBuilder.DropTable(
                name: "SupplierOpeningCorrections");

            migrationBuilder.DropTable(
                name: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_PaymentVouchers_SupplierId",
                table: "PaymentVouchers");

            migrationBuilder.DropIndex(
                name: "IX_LedgerEntryLines_AccountId_SupplierId",
                table: "LedgerEntryLines");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                table: "PaymentVouchers");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                table: "LedgerEntryLines");
        }
    }
}
