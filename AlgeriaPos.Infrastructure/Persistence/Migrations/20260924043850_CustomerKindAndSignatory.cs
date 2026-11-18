using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CustomerKindAndSignatory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SignatoryIdNumber",
                table: "RentalContracts",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignatoryName",
                table: "RentalContracts",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            // False on every contract that exists at this moment: those were raised — and, for the
            // locked ones, signed and acted on — under rules that did not ask for a signatory, and
            // refusing their first issue note now would stop goods a customer already agreed to
            // hire. RentalDocuments.CreateContractAsync sets it true from here on; nothing else
            // changes it, so a pre-release draft edited later stays exempt too. SignatoryName and
            // SignatoryIdNumber stay null on them: there is no document to recover a name from, and
            // the contract screen names them for what they are, contracts that predate the rule.
            migrationBuilder.AddColumn<bool>(
                name: "SignatoryRequired",
                table: "RentalContracts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "IdDocumentNumber",
                table: "Customers",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            // 0 = Unspecified for every customer that exists at this moment, on purpose and without
            // a backfill. Guessing a kind from the tax id or from SARL/EURL in the name would be
            // right often enough to be trusted and wrong often enough to matter: a company recorded
            // as a person has no representative history, and a person recorded as a company is
            // asked for one. The customers screen shows the kind as not set, in words, and asks;
            // the migration never decides for the owner. The default is the value we want on every
            // existing row, so there is nothing to UPDATE.
            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "Customers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "CustomerRepresentatives",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FullName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IdDocumentNumber = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    FromUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ToUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRepresentatives", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerRepresentatives_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRepresentatives_CustomerId_ToUtc",
                table: "CustomerRepresentatives",
                columns: new[] { "CustomerId", "ToUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerRepresentatives");

            migrationBuilder.DropColumn(
                name: "SignatoryIdNumber",
                table: "RentalContracts");

            migrationBuilder.DropColumn(
                name: "SignatoryName",
                table: "RentalContracts");

            migrationBuilder.DropColumn(
                name: "SignatoryRequired",
                table: "RentalContracts");

            migrationBuilder.DropColumn(
                name: "IdDocumentNumber",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "Customers");
        }
    }
}
