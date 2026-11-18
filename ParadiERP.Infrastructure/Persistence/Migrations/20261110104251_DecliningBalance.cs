using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Infrastructure.Persistence.Migrations
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
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "FixedAssetMethods",
                columns: table => new
                {
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Method = table.Column<int>(type: "INTEGER", nullable: true),
                    DecliningCoefficient = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    SwitchToStraightLine = table.Column<bool>(type: "INTEGER", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
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
