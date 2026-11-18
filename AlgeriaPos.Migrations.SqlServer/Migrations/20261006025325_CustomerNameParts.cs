using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class CustomerNameParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FamilyName",
                table: "Customers",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirstName",
                table: "Customers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            // Every person already on file keeps the name they print under (owner, 2026-10-06, the
            // employee form's rule): the whole name becomes the family name and the first name stays
            // empty, so first-name-first composition gives back the same FullName. The clerk splits it
            // the next time the person is edited; no code guesses where a name breaks. A company keeps
            // one legal name and an unspecified row has not said what it is, so neither gets parts.
            migrationBuilder.Sql("UPDATE Customers SET FamilyName = FullName WHERE Kind = 1 AND FamilyName IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FamilyName",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "FirstName",
                table: "Customers");
        }
    }
}
