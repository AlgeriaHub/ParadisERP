using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Persistence.Migrations
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
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "LateAmountWaived",
                table: "ReturnNotes",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LateDaysWaived",
                table: "ReturnNotes",
                type: "INTEGER",
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
