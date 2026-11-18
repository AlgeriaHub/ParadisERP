using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
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
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UsefulLifeMonths = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    Month = table.Column<int>(type: "INTEGER", nullable: false),
                    Generation = table.Column<int>(type: "INTEGER", nullable: false),
                    Entries = table.Column<int>(type: "INTEGER", nullable: false),
                    Indexes = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalDebit = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    PostedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PostedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ReversedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReversedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FixedAssetMonthPostings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FixedAssetMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToBranchId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Qty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    Cost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    Depreciation = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    StartMonth = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DocumentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DocumentNumber = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    By = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
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
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Treatment = table.Column<int>(type: "INTEGER", nullable: false),
                    Method = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultUsefulLifeMonths = table.Column<int>(type: "INTEGER", nullable: true),
                    ResidualPercent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    OpeningValue = table.Column<int>(type: "INTEGER", nullable: false),
                    SwitchMonth = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    SwitchedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SwitchedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SwitchEntrySourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TimeZoneId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
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
