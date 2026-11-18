using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class BranchCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "Branches",
                type: "nvarchar(2)",
                maxLength: 2,
                nullable: false,
                defaultValue: "");

            // Every location gets its two-digit code (owner, 2026-10-05): 10, 11, 12… in creation
            // order. A branch row keeps no creation time (SyncEntity carries only UpdatedAtUtc, and the
            // Version moves on every edit), so the order is the one a company was built in: the main
            // branch, then the main warehouse, then the other live locations by French name, then the
            // deleted ones (they keep a code so a number once used is never handed out again); the id
            // breaks any tie. The column is added NOT NULL with '' for the instant before this UPDATE —
            // EF's own scaffold, and it spares SQLite the table rebuild a NULL-to-NOT-NULL change needs.
            // Plain SQL that SQLite and SQL Server both read as written: a window function in a
            // correlated subquery, and VARCHAR(2), a type name both accept in CAST. A company never
            // comes near 90 locations; a 91st would not fit two digits and would stop this migration.
            migrationBuilder.Sql(@"
UPDATE Branches SET Code = CAST(9 + (
    SELECT r.Position FROM (
        SELECT b.Id, ROW_NUMBER() OVER (ORDER BY
            CASE WHEN b.IsDeleted = 0 AND b.IsMain = 1 AND b.IsWarehouse = 0 THEN 0
                 WHEN b.IsDeleted = 0 AND b.IsMain = 1 THEN 1
                 WHEN b.IsDeleted = 0 THEN 2
                 ELSE 3 END,
            b.NameFr, b.Id) AS Position
        FROM Branches b) r
    WHERE r.Id = Branches.Id) AS VARCHAR(2))");

            migrationBuilder.CreateIndex(
                name: "IX_Branches_Code",
                table: "Branches",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Branches_Code",
                table: "Branches");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "Branches");
        }
    }
}
