using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BranchContractNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A permutation of the old 10–99 range: three digits, unique even for deleted rows.
            // Coprime 137 and 900 ensure different old codes cannot map to the same code.
            migrationBuilder.Sql("UPDATE Branches SET Code = CAST(100 + ((CAST(Code AS INTEGER) - 10) * 137 + 261) % 900 AS TEXT) WHERE length(Code) = 2");
            // Every location gets a fresh version of its own, so each till pulls its new code. Without
            // the counter row (a database no app ever started on) there is no till to tell.
            migrationBuilder.Sql("UPDATE SyncCounters SET Value = Value + (SELECT COUNT(*) FROM Branches) WHERE Id = 1");
            migrationBuilder.Sql("UPDATE Branches SET Version = (SELECT Value FROM SyncCounters WHERE Id = 1) - (SELECT COUNT(*) FROM Branches b WHERE b.Code > Branches.Code), UpdatedAtUtc = CURRENT_TIMESTAMP WHERE EXISTS (SELECT 1 FROM SyncCounters WHERE Id = 1)");
            // Insert the branch code only. The complete old sequence, including its padding, stays.
            migrationBuilder.Sql("UPDATE RentalContracts SET Number = 'CTR-' || (SELECT Code FROM Branches WHERE Id = RentalContracts.BranchId) || substr(Number, 5) WHERE Number LIKE 'CTR-%' AND length(Number) >= 10 AND substr(Number, 5) NOT GLOB '*[^0-9]*'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("Branch codes and issued contract numbers cannot safely be shortened after new documents have been issued. Restore a pre-upgrade backup instead.");
        }
    }
}
