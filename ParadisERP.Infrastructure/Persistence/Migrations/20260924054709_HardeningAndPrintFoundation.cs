using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardeningAndPrintFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NULL on every account that exists at this moment, and NULL means "any terminal":
            // no cashier is locked out of the till they used yesterday by the upgrade itself.
            // Binding is a decision the admin takes per cashier on the users tab, afterwards.
            // Adding the foreign key rebuilds the Users table on SQLite (EF copies the rows into
            // a temporary table and renames it back); the unique Username index and the Version
            // index are recreated with it.
            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "Users",
                type: "TEXT",
                nullable: true);

            // The printed-document settings (docs/07 §4.3). All four nullable and left NULL: a
            // NULL text prints nothing, and there is no earlier field to carry a value over from.
            // None of them reaches StoreDto, so the Store row's Version is not bumped here and no
            // till re-pulls a row whose replicated fields did not change.
            migrationBuilder.AddColumn<string>(
                name: "BankDetails",
                table: "Stores",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DocumentTexts",
                table: "Stores",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "Stores",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Website",
                table: "Stores",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            // The buyer block frozen on the invoice (docs/07 §4.4, §8 Q12 accepted). Nullable,
            // because a column default would be a name, and then backfilled — a printed invoice
            // that exists today was printed with the customer's details as they stand today, so
            // the customer row is the best record of what the paper said. Left NULL, every
            // existing invoice would print an empty buyer block and nothing would fail.
            migrationBuilder.AddColumn<string>(
                name: "BuyerAddress",
                table: "Invoices",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuyerName",
                table: "Invoices",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BuyerTaxId",
                table: "Invoices",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            // Correlated subqueries rather than UPDATE ... JOIN: SQLite has no UPDATE FROM before
            // 3.33 and MySQL's multi-table UPDATE is its own dialect, while this statement is the
            // same text on both. Every invoice has a customer (the foreign key is Restrict), so
            // every row is filled; the WHERE only makes a re-run harmless. Mirrored word for word
            // in the MySQL set.
            migrationBuilder.Sql(
                "UPDATE Invoices SET " +
                "BuyerName = (SELECT c.FullName FROM Customers c WHERE c.Id = Invoices.CustomerId), " +
                "BuyerAddress = (SELECT c.Address FROM Customers c WHERE c.Id = Invoices.CustomerId), " +
                "BuyerTaxId = (SELECT c.TaxId FROM Customers c WHERE c.Id = Invoices.CustomerId) " +
                "WHERE BuyerName IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_BranchId",
                table: "Users",
                column: "BranchId");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Branches_BranchId",
                table: "Users",
                column: "BranchId",
                principalTable: "Branches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_Branches_BranchId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_BranchId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BankDetails",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "DocumentTexts",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "Website",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "BuyerAddress",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "BuyerName",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "BuyerTaxId",
                table: "Invoices");
        }
    }
}
