using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ParadiERP.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class ManufacturingSpecsAndSalePolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AlsoForSale",
                table: "Products",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "SpecAttributes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    NameFr = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Dimension = table.Column<int>(type: "int", nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpecAttributes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BomLineSpecs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BomLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttributeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Text = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BomLineSpecs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BomLineSpecs_BomLines_BomLineId",
                        column: x => x.BomLineId,
                        principalTable: "BomLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BomLineSpecs_SpecAttributes_AttributeId",
                        column: x => x.AttributeId,
                        principalTable: "SpecAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BomLineSpecs_UnitsOfMeasure_UnitId",
                        column: x => x.UnitId,
                        principalTable: "UnitsOfMeasure",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductSpecs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AttributeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Value = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Text = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductSpecs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductSpecs_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductSpecs_SpecAttributes_AttributeId",
                        column: x => x.AttributeId,
                        principalTable: "SpecAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductSpecs_UnitsOfMeasure_UnitId",
                        column: x => x.UnitId,
                        principalTable: "UnitsOfMeasure",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "SpecAttributes",
                columns: new[] { "Id", "Code", "CreatedAtUtc", "Dimension", "IsActive", "IsSystem", "Kind", "NameAr", "NameEn", "NameFr", "SortOrder", "UpdatedAtUtc" },
                values: new object[,]
                {
                    { new Guid("5bec0000-0000-4000-8000-000000000001"), "LENGTH", new DateTime(2026, 10, 26, 0, 0, 0, 0, DateTimeKind.Utc), 2, true, true, 0, "الطول", "Length", "Longueur", 1, new DateTime(2026, 10, 26, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("5bec0000-0000-4000-8000-000000000002"), "WIDTH", new DateTime(2026, 10, 26, 0, 0, 0, 0, DateTimeKind.Utc), 2, true, true, 0, "العرض", "Width", "Largeur", 2, new DateTime(2026, 10, 26, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("5bec0000-0000-4000-8000-000000000003"), "THICKNESS", new DateTime(2026, 10, 26, 0, 0, 0, 0, DateTimeKind.Utc), 2, true, true, 0, "السماكة", "Thickness", "Épaisseur", 3, new DateTime(2026, 10, 26, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("5bec0000-0000-4000-8000-000000000004"), "DIAMETER", new DateTime(2026, 10, 26, 0, 0, 0, 0, DateTimeKind.Utc), 2, true, true, 0, "القطر", "Diameter", "Diamètre", 4, new DateTime(2026, 10, 26, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("5bec0000-0000-4000-8000-000000000005"), "GRADE", new DateTime(2026, 10, 26, 0, 0, 0, 0, DateTimeKind.Utc), 5, true, true, 1, "الدرجة", "Grade", "Nuance", 5, new DateTime(2026, 10, 26, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.InsertData(
                table: "UnitsOfMeasure",
                columns: new[] { "Id", "Code", "CreatedAtUtc", "Dimension", "FactorToReference", "IsActive", "IsSystem", "NameAr", "NameEn", "NameFr", "UpdatedAtUtc" },
                values: new object[] { new Guid("0a17e000-0000-4000-8000-000000000007"), "mm", new DateTime(2026, 10, 25, 0, 0, 0, 0, DateTimeKind.Utc), 2, 0.001m, true, true, "مليمتر", "Millimetre", "Millimètre", new DateTime(2026, 10, 25, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.CreateIndex(
                name: "IX_BomLineSpecs_AttributeId",
                table: "BomLineSpecs",
                column: "AttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_BomLineSpecs_BomLineId_AttributeId",
                table: "BomLineSpecs",
                columns: new[] { "BomLineId", "AttributeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BomLineSpecs_UnitId",
                table: "BomLineSpecs",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductSpecs_AttributeId",
                table: "ProductSpecs",
                column: "AttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductSpecs_ProductId_AttributeId",
                table: "ProductSpecs",
                columns: new[] { "ProductId", "AttributeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductSpecs_UnitId",
                table: "ProductSpecs",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_SpecAttributes_Code",
                table: "SpecAttributes",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BomLineSpecs");

            migrationBuilder.DropTable(
                name: "ProductSpecs");

            migrationBuilder.DropTable(
                name: "SpecAttributes");

            migrationBuilder.DeleteData(
                table: "UnitsOfMeasure",
                keyColumn: "Id",
                keyValue: new Guid("0a17e000-0000-4000-8000-000000000007"));

            migrationBuilder.DropColumn(
                name: "AlsoForSale",
                table: "Products");
        }
    }
}
