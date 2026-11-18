using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Ledger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every column this migration adds to an existing table is nullable, and null is
            // the right answer for the rows already there: those stock movements genuinely
            // belonged to no document, because documents did not exist when they were made.
            // Backfilling them with an invented id would claim a receiving note that nobody
            // ever raised.
            //
            // The three new tables start empty on purpose. Nothing is posted retroactively
            // for sales, receiving notes or remittances recorded before this release: the
            // costs, tenders and cash positions behind them were never captured in a form the
            // posting rules can read, and a ledger assembled from guesses is worse than one
            // that plainly starts on a date. A company adopting this posts its position with
            // opening balances, which is exactly the document that exists for it.
            migrationBuilder.AddColumn<Guid>(
                name: "DocumentId",
                table: "StockMovements",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LedgerAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    NameAr = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NameFr = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    IsSystem = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LedgerEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    IsReversal = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReversalOfEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsInterBranch = table.Column<bool>(type: "INTEGER", nullable: false),
                    CounterpartBranchId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LedgerEntryLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Debit = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    Credit = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerEntryLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LedgerEntryLines_LedgerAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "LedgerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LedgerEntryLines_LedgerEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "LedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_DocumentId",
                table: "StockMovements",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerAccounts_Code",
                table: "LedgerAccounts",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_EntryDate_BranchId",
                table: "LedgerEntries",
                columns: new[] { "EntryDate", "BranchId" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_SourceType_SourceId",
                table: "LedgerEntries",
                columns: new[] { "SourceType", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntryLines_AccountId_BranchId",
                table: "LedgerEntryLines",
                columns: new[] { "AccountId", "BranchId" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntryLines_EntryId",
                table: "LedgerEntryLines",
                column: "EntryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LedgerEntryLines");

            migrationBuilder.DropTable(
                name: "LedgerAccounts");

            migrationBuilder.DropTable(
                name: "LedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_DocumentId",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "DocumentId",
                table: "StockMovements");
        }
    }
}
