using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class RentalMinimumDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RentalMinimumDays",
                table: "Stores",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MinimumRentalDays",
                table: "RentalContracts",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RentalMinimumDays",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "MinimumRentalDays",
                table: "RentalContracts");
        }
    }
}
