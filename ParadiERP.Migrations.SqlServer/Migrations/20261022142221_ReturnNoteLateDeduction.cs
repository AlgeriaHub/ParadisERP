using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class ReturnNoteLateDeduction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LateAmountFull",
                table: "ReturnNotes",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LateAmountWaived",
                table: "ReturnNotes",
                type: "decimal(18,3)",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LateDaysWaived",
                table: "ReturnNotes",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LateAmountFull",
                table: "ReturnNotes");

            migrationBuilder.DropColumn(
                name: "LateAmountWaived",
                table: "ReturnNotes");

            migrationBuilder.DropColumn(
                name: "LateDaysWaived",
                table: "ReturnNotes");
        }
    }
}
