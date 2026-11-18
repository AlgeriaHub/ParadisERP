using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class DecliningBalance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Declining balance (2026-11-10): the switch to straight line is on by default, so a saved settings row takes it on.
            // The method itself stays StraightLine on every existing row: existing assets keep today's depreciation.
            migrationBuilder.AddColumn<bool>(
                name: "SwitchToStraightLine",
                table: "FixedAssetSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "FixedAssetMethods",
                columns: table => new
                {
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Method = table.Column<int>(type: "int", nullable: true),
                    DecliningCoefficient = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    SwitchToStraightLine = table.Column<bool>(type: "bit", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FixedAssetMethods", x => x.ProductId);
                    table.ForeignKey(
                        name: "FK_FixedAssetMethods_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FixedAssetMethods");

            migrationBuilder.DropColumn(
                name: "SwitchToStraightLine",
                table: "FixedAssetSettings");
        }
    }
}
