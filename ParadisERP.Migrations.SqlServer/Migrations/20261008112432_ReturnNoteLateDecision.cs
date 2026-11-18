using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Migrations.SqlServer.Migrations
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
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LateWaiverReason",
                table: "ReturnNotes",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WaiveOverdue",
                table: "ReturnNotes",
                type: "bit",
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
