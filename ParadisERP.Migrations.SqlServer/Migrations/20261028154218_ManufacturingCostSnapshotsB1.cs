using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class ManufacturingCostSnapshotsB1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManufacturingCostSheets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CalculationVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RequestId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AcceptedBaseQty = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    RejectedBaseQty = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    TotalCost = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    ProvisionalReasons = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManufacturingCostSheets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManufacturingCostSheets_ProductionLines_LineId",
                        column: x => x.LineId,
                        principalTable: "ProductionLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ManufacturingCostSheets_ProductionOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "ProductionOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ManufacturingCostElementLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SheetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Element = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    TotalCost = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceReference = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManufacturingCostElementLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManufacturingCostElementLines_ManufacturingCostSheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "ManufacturingCostSheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ManufacturingExpenseAllocationSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SheetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExpenseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PaymentVoucherId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManufacturingExpenseAllocationSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManufacturingExpenseAllocationSnapshots_ManufacturingCostSheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "ManufacturingCostSheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ManufacturingLabourRateSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SheetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PayrollYear = table.Column<int>(type: "int", nullable: false),
                    PayrollMonth = table.Column<int>(type: "int", nullable: false),
                    Hours = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: true),
                    TotalCost = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManufacturingLabourRateSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManufacturingLabourRateSnapshots_ManufacturingCostSheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "ManufacturingCostSheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ManufacturingLateCosts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SheetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceReference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManufacturingLateCosts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManufacturingLateCosts_ManufacturingCostSheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "ManufacturingCostSheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ManufacturingMaterialCostSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SheetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OrderMaterialId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    TotalCost = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    CostBasis = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManufacturingMaterialCostSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManufacturingMaterialCostSnapshots_ManufacturingCostSheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "ManufacturingCostSheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ManufacturingReleaseValueSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SheetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OutputReleaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    Value = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitValue = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManufacturingReleaseValueSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManufacturingReleaseValueSnapshots_ManufacturingCostSheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "ManufacturingCostSheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ManufacturingScrapRecoverySnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SheetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RejectedQty = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    RecoveryValue = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManufacturingScrapRecoverySnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManufacturingScrapRecoverySnapshots_ManufacturingCostSheets_SheetId",
                        column: x => x.SheetId,
                        principalTable: "ManufacturingCostSheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingCostElementLines_SheetId_Element",
                table: "ManufacturingCostElementLines",
                columns: new[] { "SheetId", "Element" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingCostSheets_LineId",
                table: "ManufacturingCostSheets",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingCostSheets_OrderId",
                table: "ManufacturingCostSheets",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingCostSheets_RequestId",
                table: "ManufacturingCostSheets",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingExpenseAllocationSnapshots_SheetId_ExpenseId",
                table: "ManufacturingExpenseAllocationSnapshots",
                columns: new[] { "SheetId", "ExpenseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingLabourRateSnapshots_SheetId_EmployeeId_PayrollYear_PayrollMonth",
                table: "ManufacturingLabourRateSnapshots",
                columns: new[] { "SheetId", "EmployeeId", "PayrollYear", "PayrollMonth" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingLateCosts_SheetId_Kind_SourceReference",
                table: "ManufacturingLateCosts",
                columns: new[] { "SheetId", "Kind", "SourceReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingMaterialCostSnapshots_SheetId_OrderMaterialId",
                table: "ManufacturingMaterialCostSnapshots",
                columns: new[] { "SheetId", "OrderMaterialId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingReleaseValueSnapshots_SheetId_OutputReleaseId",
                table: "ManufacturingReleaseValueSnapshots",
                columns: new[] { "SheetId", "OutputReleaseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingScrapRecoverySnapshots_SheetId",
                table: "ManufacturingScrapRecoverySnapshots",
                column: "SheetId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManufacturingCostElementLines");

            migrationBuilder.DropTable(
                name: "ManufacturingExpenseAllocationSnapshots");

            migrationBuilder.DropTable(
                name: "ManufacturingLabourRateSnapshots");

            migrationBuilder.DropTable(
                name: "ManufacturingLateCosts");

            migrationBuilder.DropTable(
                name: "ManufacturingMaterialCostSnapshots");

            migrationBuilder.DropTable(
                name: "ManufacturingReleaseValueSnapshots");

            migrationBuilder.DropTable(
                name: "ManufacturingScrapRecoverySnapshots");

            migrationBuilder.DropTable(
                name: "ManufacturingCostSheets");
        }
    }
}
