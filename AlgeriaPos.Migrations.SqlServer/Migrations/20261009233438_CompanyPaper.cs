using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class CompanyPaper : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The company's own paper (Facture N° 45 and contract 12547, 2026-10-10): a customer's
            // other fiscal identifiers, frozen on the invoice with the planned way of paying, the
            // days a hire line is billed for, and each line's place on the paper. Every existing
            // customer gets '{}', as the branch column did; every existing invoice reads NULL — "not
            // recorded" — and every existing line no days, which multiplies by one, and position 0,
            // so no stored figure changes.
            migrationBuilder.AddColumn<string>(
                name: "BuyerFiscalIds",
                table: "Invoices",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethodKey",
                table: "Invoices",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Days",
                table: "InvoiceLines",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "InvoiceLines",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FiscalIds",
                table: "Customers",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<int>(
                name: "Days",
                table: "CreditNoteLines",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "CreditNoteLines",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BuyerFiscalIds",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "PaymentMethodKey",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Days",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "FiscalIds",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "Days",
                table: "CreditNoteLines");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "CreditNoteLines");
        }
    }
}
