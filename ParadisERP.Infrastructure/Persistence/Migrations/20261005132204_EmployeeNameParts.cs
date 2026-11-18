using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmployeeNameParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FamilyName",
                table: "Employees",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FirstName",
                table: "Employees",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            // Everyone already on file keeps the name they print under (owner, PinSay #301: "leave it
            // for the admin"): the whole name becomes the family name and the first name stays empty,
            // so first-name-first composition gives back exactly the same FullName. An admin splits it
            // the next time the person is edited; no code guesses where a name breaks.
            migrationBuilder.Sql("UPDATE Employees SET FamilyName = FullName WHERE FamilyName IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FamilyName",
                table: "Employees");

            migrationBuilder.DropColumn(
                name: "FirstName",
                table: "Employees");
        }
    }
}
