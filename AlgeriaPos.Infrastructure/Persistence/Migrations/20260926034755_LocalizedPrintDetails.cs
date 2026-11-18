using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LocalizedPrintDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddressAr",
                table: "Stores",
                type: "TEXT",
                maxLength: 400,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AddressEn",
                table: "Stores",
                type: "TEXT",
                maxLength: 400,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AddressFr",
                table: "Stores",
                type: "TEXT",
                maxLength: 400,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReceiptFooterAr",
                table: "Stores",
                type: "TEXT",
                maxLength: 400,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReceiptFooterEn",
                table: "Stores",
                type: "TEXT",
                maxLength: 400,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ReceiptFooterFr",
                table: "Stores",
                type: "TEXT",
                maxLength: 400,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AddressAr",
                table: "Branches",
                type: "TEXT",
                maxLength: 400,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AddressEn",
                table: "Branches",
                type: "TEXT",
                maxLength: 400,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AddressFr",
                table: "Branches",
                type: "TEXT",
                maxLength: 400,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("UPDATE Stores SET AddressFr = Address WHERE AddressFr = '';");
            migrationBuilder.Sql("UPDATE Stores SET ReceiptFooterFr = ReceiptFooter WHERE ReceiptFooterFr = '';");
            migrationBuilder.Sql("UPDATE Branches SET AddressFr = Address WHERE AddressFr = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddressAr",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "AddressEn",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "AddressFr",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "ReceiptFooterAr",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "ReceiptFooterEn",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "ReceiptFooterFr",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "AddressAr",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "AddressEn",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "AddressFr",
                table: "Branches");
        }
    }
}
