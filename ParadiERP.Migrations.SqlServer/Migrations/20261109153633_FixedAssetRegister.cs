using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class FixedAssetRegister : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FixedAssetLives",
                columns: table => new
                {
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsefulLifeMonths = table.Column<int>(type: "int", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FixedAssetLives", x => x.ProductId);
                    table.ForeignKey(
                        name: "FK_FixedAssetLives_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FixedAssetMonthPostings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    Generation = table.Column<int>(type: "int", nullable: false),
                    Entries = table.Column<int>(type: "int", nullable: false),
                    Indexes = table.Column<int>(type: "int", nullable: false),
                    TotalDebit = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    PostedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PostedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReversedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReversedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FixedAssetMonthPostings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FixedAssetMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ToBranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Qty = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    Cost = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Depreciation = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    StartMonth = table.Column<DateOnly>(type: "date", nullable: true),
                    AtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DocumentNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SourceType = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    By = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FixedAssetMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FixedAssetMovements_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FixedAssetMovements_Branches_ToBranchId",
                        column: x => x.ToBranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FixedAssetMovements_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FixedAssetSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Treatment = table.Column<int>(type: "int", nullable: false),
                    Method = table.Column<int>(type: "int", nullable: false),
                    DefaultUsefulLifeMonths = table.Column<int>(type: "int", nullable: true),
                    ResidualPercent = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    OpeningValue = table.Column<int>(type: "int", nullable: false),
                    SwitchMonth = table.Column<DateOnly>(type: "date", nullable: true),
                    SwitchedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SwitchedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SwitchEntrySourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TimeZoneId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FixedAssetSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FixedAssetMonthPostings_Year_Month_Generation",
                table: "FixedAssetMonthPostings",
                columns: new[] { "Year", "Month", "Generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FixedAssetMovements_AtUtc",
                table: "FixedAssetMovements",
                column: "AtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_FixedAssetMovements_BranchId",
                table: "FixedAssetMovements",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_FixedAssetMovements_DocumentId",
                table: "FixedAssetMovements",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_FixedAssetMovements_ProductId_AtUtc",
                table: "FixedAssetMovements",
                columns: new[] { "ProductId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_FixedAssetMovements_ToBranchId",
                table: "FixedAssetMovements",
                column: "ToBranchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FixedAssetLives");

            migrationBuilder.DropTable(
                name: "FixedAssetMonthPostings");

            migrationBuilder.DropTable(
                name: "FixedAssetMovements");

            migrationBuilder.DropTable(
                name: "FixedAssetSettings");
        }
    }
}
