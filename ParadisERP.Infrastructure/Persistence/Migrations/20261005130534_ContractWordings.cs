using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ContractWordings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContractWordings",
                table: "Stores",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WordingKey",
                table: "RentalContracts",
                type: "TEXT",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContractWordings",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "WordingKey",
                table: "RentalContracts");
        }
    }
}
