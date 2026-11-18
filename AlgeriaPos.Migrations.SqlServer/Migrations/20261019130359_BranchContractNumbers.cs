using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class BranchContractNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Code",
                table: "Branches",
                type: "nvarchar(3)",
                maxLength: 3,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(2)",
                oldMaxLength: 2);
            // The same statements as the SQLite migration, in T-SQL: a permutation of the old 10–99
            // range (137 and 900 are coprime, so no two old codes meet), a fresh version for every
            // location so each till pulls its new code, then the branch code inserted into each
            // contract number with its whole old sequence kept.
            migrationBuilder.Sql("UPDATE Branches SET Code = CAST(100 + ((CAST(Code AS int) - 10) * 137 + 261) % 900 AS varchar(3)) WHERE LEN(Code) = 2");
            migrationBuilder.Sql("UPDATE SyncCounters SET Value = Value + (SELECT COUNT(*) FROM Branches) WHERE Id = 1");
            migrationBuilder.Sql("UPDATE Branches SET Version = (SELECT Value FROM SyncCounters WHERE Id = 1) - (SELECT COUNT(*) FROM Branches b WHERE b.Code > Branches.Code), UpdatedAtUtc = SYSUTCDATETIME() WHERE EXISTS (SELECT 1 FROM SyncCounters WHERE Id = 1)");
            migrationBuilder.Sql("UPDATE RentalContracts SET Number = 'CTR-' + (SELECT Code FROM Branches WHERE Id = RentalContracts.BranchId) + SUBSTRING(Number, 5, 32) WHERE Number LIKE 'CTR-%' AND LEN(Number) >= 10 AND SUBSTRING(Number, 5, 32) NOT LIKE '%[^0-9]%'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.NotSupportedException("Branch codes and issued contract numbers cannot safely be shortened after new documents have been issued. Restore a pre-upgrade backup instead.");
        }
    }
}
