using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Moves the Algerian fiscal identifiers into a per-country key/value column and drops the
    /// currency/tax settings that now live in the operator-owned country profile. Existing NIF,
    /// NIS, RC and AI values are copied, not lost.
    /// </summary>
    public partial class CountryProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FiscalIds",
                table: "Stores",
                type: "TEXT",
                maxLength: 2000,
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.Sql(@"UPDATE ""Stores"" SET ""FiscalIds"" = json_object('nif', ""Nif"", 'nis', ""Nis"", 'rc', ""Rc"", 'ai', ""Ai"");");

            migrationBuilder.DropColumn(name: "Nif", table: "Stores");
            migrationBuilder.DropColumn(name: "Nis", table: "Stores");
            migrationBuilder.DropColumn(name: "Rc", table: "Stores");
            migrationBuilder.DropColumn(name: "Ai", table: "Stores");
            migrationBuilder.DropColumn(name: "CurrencyCode", table: "Stores");
            migrationBuilder.DropColumn(name: "PricesIncludeTax", table: "Stores");

            migrationBuilder.RenameColumn(name: "RequireCustomerNif", table: "Stores", newName: "RequireCustomerTaxId");
            migrationBuilder.RenameColumn(name: "Nif", table: "Customers", newName: "TaxId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Nif",
                table: "Stores",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "");
            migrationBuilder.AddColumn<string>(
                name: "Nis",
                table: "Stores",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "");
            migrationBuilder.AddColumn<string>(
                name: "Rc",
                table: "Stores",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "");
            migrationBuilder.AddColumn<string>(
                name: "Ai",
                table: "Stores",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "");
            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                table: "Stores",
                type: "TEXT",
                maxLength: 8,
                nullable: false,
                defaultValue: "DZD");
            migrationBuilder.AddColumn<bool>(
                name: "PricesIncludeTax",
                table: "Stores",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql(@"UPDATE ""Stores"" SET ""Nif"" = COALESCE(json_extract(""FiscalIds"", '$.nif'), ''), ""Nis"" = COALESCE(json_extract(""FiscalIds"", '$.nis'), ''), ""Rc"" = COALESCE(json_extract(""FiscalIds"", '$.rc'), ''), ""Ai"" = COALESCE(json_extract(""FiscalIds"", '$.ai'), '');");

            migrationBuilder.DropColumn(name: "FiscalIds", table: "Stores");
            migrationBuilder.RenameColumn(name: "RequireCustomerTaxId", table: "Stores", newName: "RequireCustomerNif");
            migrationBuilder.RenameColumn(name: "TaxId", table: "Customers", newName: "Nif");
        }
    }
}
