using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VoucherContracts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VoucherContractCategories",
                table: "Stores",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ContractId",
                table: "ReceiptVouchers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "ReceiptVouchers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<Guid>(
                name: "ReturnNoteId",
                table: "ReceiptVouchers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceiptVouchers_ContractId",
                table: "ReceiptVouchers",
                column: "ContractId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReceiptVouchers_ContractId",
                table: "ReceiptVouchers");

            migrationBuilder.DropColumn(
                name: "VoucherContractCategories",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "ContractId",
                table: "ReceiptVouchers");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "ReceiptVouchers");

            migrationBuilder.DropColumn(
                name: "ReturnNoteId",
                table: "ReceiptVouchers");
        }
    }
}
