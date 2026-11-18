using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReturnNoteLateDecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LateDecidedBy",
                table: "ReturnNotes",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LateWaiverReason",
                table: "ReturnNotes",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WaiveOverdue",
                table: "ReturnNotes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LateDecidedBy",
                table: "ReturnNotes");

            migrationBuilder.DropColumn(
                name: "LateWaiverReason",
                table: "ReturnNotes");

            migrationBuilder.DropColumn(
                name: "WaiveOverdue",
                table: "ReturnNotes");
        }
    }
}
