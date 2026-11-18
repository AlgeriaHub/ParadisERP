using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompanyBranchKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsWarehouse",
                table: "Branches",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Until now IsMain meant "the central warehouse". Since 2026-10-05 a location is of one of
            // two kinds — a branch or a warehouse — and IsMain means "the primary one of its kind"
            // (owner: one primary branch, one primary warehouse). A deleted row keeps no flag, so the
            // index below can only ever see live primaries.
            migrationBuilder.Sql("UPDATE Branches SET IsMain = 0 WHERE IsDeleted = 1");
            // History-safe (owner, 2026-10-05): the old main location becomes the primary warehouse only
            // when nothing that belongs to a selling branch points at it — no sale, document, cash
            // movement, ledger line or staff record. Such rows keep their branch meaning for good, so
            // a location that has any stays a branch (it is then the primary branch, and
            // DefaultBranches creates the primary warehouse beside it). No historical row is ever
            // re-pointed; only this one row's flag changes. Its stock and opening movements stay on it
            // either way. Plain SQL that SQLite and SQL Server both read as written.
            migrationBuilder.Sql(@"
UPDATE Branches SET IsWarehouse = 1
WHERE IsMain = 1 AND IsDeleted = 0
  AND NOT EXISTS (SELECT 1 FROM Sales x WHERE x.BranchId = Branches.Id)
  AND NOT EXISTS (SELECT 1 FROM Invoices x WHERE x.BranchId = Branches.Id)
  AND NOT EXISTS (SELECT 1 FROM CreditNotes x WHERE x.BranchId = Branches.Id)
  AND NOT EXISTS (SELECT 1 FROM ReceiptVouchers x WHERE x.BranchId = Branches.Id)
  AND NOT EXISTS (SELECT 1 FROM PaymentVouchers x WHERE x.BranchId = Branches.Id)
  AND NOT EXISTS (SELECT 1 FROM RentalContracts x WHERE x.BranchId = Branches.Id)
  AND NOT EXISTS (SELECT 1 FROM IssueNotes x WHERE x.BranchId = Branches.Id)
  AND NOT EXISTS (SELECT 1 FROM ReturnNotes x WHERE x.BranchId = Branches.Id)
  AND NOT EXISTS (SELECT 1 FROM CashMovements m INNER JOIN CashBoxes c ON c.Id = m.CashBoxId WHERE c.BranchId = Branches.Id)
  AND NOT EXISTS (SELECT 1 FROM LedgerEntries x WHERE x.BranchId = Branches.Id OR x.CounterpartBranchId = Branches.Id)
  AND NOT EXISTS (SELECT 1 FROM LedgerEntryLines x WHERE x.BranchId = Branches.Id)
  AND NOT EXISTS (SELECT 1 FROM Employees x WHERE x.BranchId = Branches.Id)
  AND NOT EXISTS (SELECT 1 FROM PayrollRuns x WHERE x.BranchId = Branches.Id)
  AND NOT EXISTS (SELECT 1 FROM PayrollRunLines x WHERE x.BranchIdSnapshot = Branches.Id)
  AND NOT EXISTS (SELECT 1 FROM PayrollStatutoryPayments x WHERE x.BranchId = Branches.Id)
  AND NOT EXISTS (SELECT 1 FROM WageAdvances x WHERE x.BranchId = Branches.Id)
  AND NOT EXISTS (SELECT 1 FROM WagePayments x WHERE x.BranchId = Branches.Id)");

            migrationBuilder.CreateIndex(
                name: "IX_Branches_OnePrimaryPerKind",
                table: "Branches",
                column: "IsWarehouse",
                unique: true,
                filter: "IsMain = 1 AND IsDeleted = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Branches_OnePrimaryPerKind",
                table: "Branches");

            // Back to one flag meaning "the central warehouse": the primary warehouse keeps it.
            migrationBuilder.Sql("UPDATE Branches SET IsMain = CASE WHEN IsWarehouse = 1 AND IsMain = 1 THEN 1 ELSE 0 END");

            migrationBuilder.DropColumn(
                name: "IsWarehouse",
                table: "Branches");
        }
    }
}
