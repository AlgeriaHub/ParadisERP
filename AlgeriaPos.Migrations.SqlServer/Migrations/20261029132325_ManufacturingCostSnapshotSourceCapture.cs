using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class ManufacturingCostSnapshotSourceCapture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceHash",
                table: "ManufacturingCostSheets",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SourceSnapshotJson",
                table: "ManufacturingCostSheets",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceHash",
                table: "ManufacturingCostSheets");

            migrationBuilder.DropColumn(
                name: "SourceSnapshotJson",
                table: "ManufacturingCostSheets");
        }
    }
}
