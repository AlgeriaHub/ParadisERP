using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ManufacturingPhase2Orders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LaborBookingChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BookingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    LogRowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<int>(type: "INTEGER", nullable: false),
                    Hours = table.Column<decimal>(type: "TEXT", precision: 9, scale: 2, nullable: false),
                    OverrideReason = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaborBookingChanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductionLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    LineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ReturnedReason = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SubmittedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SubmittedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SubmittedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ApprovedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionLogs_ProductionLines_LineId",
                        column: x => x.LineId,
                        principalTable: "ProductionLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    LineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BomId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BomVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PlannedQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    PlannedUnitId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PlannedBaseQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    Batches = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    PlannedStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PlannedEnd = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedShort = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShortageReason = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    AcceptedBaseQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    RejectedBaseQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    EstimatedMaterialCost = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ClientRequestId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletionRequestedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CompletionRequestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CompletedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ActualEnd = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionOrders_BillsOfMaterials_BomId",
                        column: x => x.BomId,
                        principalTable: "BillsOfMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionOrders_Branches_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionOrders_ProductionLines_LineId",
                        column: x => x.LineId,
                        principalTable: "ProductionLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionOrders_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionOrders_UnitsOfMeasure_PlannedUnitId",
                        column: x => x.PlannedUnitId,
                        principalTable: "UnitsOfMeasure",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaterialMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientRequestId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    PostingState = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaterialMovements_ProductionOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "ProductionOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OutputReleases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LocationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnteredQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    UnitId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Factor = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    BaseQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    UnitCostSnapshot = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    StockMovementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClientRequestId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    PostingState = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutputReleases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutputReleases_ProductionOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "ProductionOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OutputReleases_StockMovements_StockMovementId",
                        column: x => x.StockMovementId,
                        principalTable: "StockMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OutputReleases_UnitsOfMeasure_UnitId",
                        column: x => x.UnitId,
                        principalTable: "UnitsOfMeasure",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionOrderEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionOrderEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionOrderEvents_ProductionOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "ProductionOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionOrderExpenses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    PaymentVoucherId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClientRequestId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionOrderExpenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionOrderExpenses_PaymentVouchers_PaymentVoucherId",
                        column: x => x.PaymentVoucherId,
                        principalTable: "PaymentVouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionOrderExpenses_ProductionOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "ProductionOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionOrderMaterials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    BomLineId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseUnitId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NetBaseQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    WasteBaseQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    RequiredBaseQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    EstimatedUnitCost = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    Specification = table.Column<string>(type: "TEXT", maxLength: 600, nullable: true),
                    IssuedBaseQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    ReturnedBaseQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    ConsumedBaseQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    ScrappedBaseQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    ReservationReleasedBaseQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionOrderMaterials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionOrderMaterials_ProductionOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "ProductionOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductionOrderMaterials_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionOrderMaterials_UnitsOfMeasure_BaseUnitId",
                        column: x => x.BaseUnitId,
                        principalTable: "UnitsOfMeasure",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionOrderStages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    NameAr = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NameFr = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PlannedMinutes = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    ExpectedWorkers = table.Column<int>(type: "INTEGER", nullable: false),
                    IsQualityCheckpoint = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsOutput = table.Column<bool>(type: "INTEGER", nullable: false),
                    GoodBaseQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    RejectedBaseQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionOrderStages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionOrderStages_ProductionOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "ProductionOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MaterialMovementLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MovementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderMaterialId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EnteredQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    UnitId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Factor = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    BaseQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    UnitCostSnapshot = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    StockMovementId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialMovementLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaterialMovementLines_MaterialMovements_MovementId",
                        column: x => x.MovementId,
                        principalTable: "MaterialMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_MaterialMovementLines_ProductionOrderMaterials_OrderMaterialId",
                        column: x => x.OrderMaterialId,
                        principalTable: "ProductionOrderMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialMovementLines_StockMovements_StockMovementId",
                        column: x => x.StockMovementId,
                        principalTable: "StockMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialMovementLines_UnitsOfMeasure_UnitId",
                        column: x => x.UnitId,
                        principalTable: "UnitsOfMeasure",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionLogRows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LogId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Category = table.Column<int>(type: "INTEGER", nullable: true),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Specification = table.Column<string>(type: "TEXT", maxLength: 600, nullable: true),
                    PlannedQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    CompletedQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    RejectedQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    RejectReason = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionLogRows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionLogRows_ProductionLogs_LogId",
                        column: x => x.LogId,
                        principalTable: "ProductionLogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductionLogRows_ProductionOrderStages_StageId",
                        column: x => x.StageId,
                        principalTable: "ProductionOrderStages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionLogRows_ProductionOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "ProductionOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LaborBookings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Day = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    OrderKind = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StageId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LogRowId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Hours = table.Column<decimal>(type: "TEXT", precision: 9, scale: 2, nullable: false),
                    PayrollBranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PayrollYear = table.Column<int>(type: "INTEGER", nullable: false),
                    PayrollMonth = table.Column<int>(type: "INTEGER", nullable: false),
                    AttendanceHours = table.Column<decimal>(type: "TEXT", precision: 9, scale: 2, nullable: false),
                    OverrideReason = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    OverriddenByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OverriddenBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LaborBookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LaborBookings_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaborBookings_ProductionLines_LineId",
                        column: x => x.LineId,
                        principalTable: "ProductionLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LaborBookings_ProductionLogRows_LogRowId",
                        column: x => x.LogRowId,
                        principalTable: "ProductionLogRows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaterialConsumptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderMaterialId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LogRowId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BaseQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    Reason = table.Column<int>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    ClientRequestId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialConsumptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaterialConsumptions_ProductionLogRows_LogRowId",
                        column: x => x.LogRowId,
                        principalTable: "ProductionLogRows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialConsumptions_ProductionOrderMaterials_OrderMaterialId",
                        column: x => x.OrderMaterialId,
                        principalTable: "ProductionOrderMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaterialConsumptions_ProductionOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "ProductionOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LaborBookingChanges_EmployeeId_Day",
                table: "LaborBookingChanges",
                columns: new[] { "EmployeeId", "Day" });

            migrationBuilder.CreateIndex(
                name: "IX_LaborBookings_EmployeeId_Day",
                table: "LaborBookings",
                columns: new[] { "EmployeeId", "Day" });

            migrationBuilder.CreateIndex(
                name: "IX_LaborBookings_EmployeeId_LogRowId",
                table: "LaborBookings",
                columns: new[] { "EmployeeId", "LogRowId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LaborBookings_LineId",
                table: "LaborBookings",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborBookings_LogRowId",
                table: "LaborBookings",
                column: "LogRowId");

            migrationBuilder.CreateIndex(
                name: "IX_LaborBookings_OrderId_StageId",
                table: "LaborBookings",
                columns: new[] { "OrderId", "StageId" });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialConsumptions_ClientRequestId",
                table: "MaterialConsumptions",
                column: "ClientRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaterialConsumptions_LogRowId",
                table: "MaterialConsumptions",
                column: "LogRowId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialConsumptions_OrderId",
                table: "MaterialConsumptions",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialConsumptions_OrderMaterialId",
                table: "MaterialConsumptions",
                column: "OrderMaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialMovementLines_MovementId",
                table: "MaterialMovementLines",
                column: "MovementId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialMovementLines_OrderMaterialId",
                table: "MaterialMovementLines",
                column: "OrderMaterialId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialMovementLines_StockMovementId",
                table: "MaterialMovementLines",
                column: "StockMovementId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialMovementLines_UnitId",
                table: "MaterialMovementLines",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialMovements_ClientRequestId",
                table: "MaterialMovements",
                column: "ClientRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaterialMovements_LineId_AtUtc",
                table: "MaterialMovements",
                columns: new[] { "LineId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MaterialMovements_Number",
                table: "MaterialMovements",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaterialMovements_OrderId_AtUtc",
                table: "MaterialMovements",
                columns: new[] { "OrderId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutputReleases_ClientRequestId",
                table: "OutputReleases",
                column: "ClientRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutputReleases_Number",
                table: "OutputReleases",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutputReleases_OrderId",
                table: "OutputReleases",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_OutputReleases_StockMovementId",
                table: "OutputReleases",
                column: "StockMovementId");

            migrationBuilder.CreateIndex(
                name: "IX_OutputReleases_UnitId",
                table: "OutputReleases",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLogRows_LogId",
                table: "ProductionLogRows",
                column: "LogId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLogRows_OrderId",
                table: "ProductionLogRows",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLogRows_StageId",
                table: "ProductionLogRows",
                column: "StageId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLogs_LineId_Day",
                table: "ProductionLogs",
                columns: new[] { "LineId", "Day" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLogs_Number",
                table: "ProductionLogs",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrderEvents_OrderId_AtUtc",
                table: "ProductionOrderEvents",
                columns: new[] { "OrderId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrderExpenses_ClientRequestId",
                table: "ProductionOrderExpenses",
                column: "ClientRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrderExpenses_OrderId",
                table: "ProductionOrderExpenses",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrderExpenses_PaymentVoucherId",
                table: "ProductionOrderExpenses",
                column: "PaymentVoucherId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrderMaterials_BaseUnitId",
                table: "ProductionOrderMaterials",
                column: "BaseUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrderMaterials_OrderId",
                table: "ProductionOrderMaterials",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrderMaterials_ProductId",
                table: "ProductionOrderMaterials",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_BomId",
                table: "ProductionOrders",
                column: "BomId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_ClientRequestId",
                table: "ProductionOrders",
                column: "ClientRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_LineId_Status",
                table: "ProductionOrders",
                columns: new[] { "LineId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_LocationId_Status",
                table: "ProductionOrders",
                columns: new[] { "LocationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_Number",
                table: "ProductionOrders",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_PlannedUnitId",
                table: "ProductionOrders",
                column: "PlannedUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrders_ProductId",
                table: "ProductionOrders",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionOrderStages_OrderId_Sequence",
                table: "ProductionOrderStages",
                columns: new[] { "OrderId", "Sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LaborBookingChanges");

            migrationBuilder.DropTable(
                name: "LaborBookings");

            migrationBuilder.DropTable(
                name: "MaterialConsumptions");

            migrationBuilder.DropTable(
                name: "MaterialMovementLines");

            migrationBuilder.DropTable(
                name: "OutputReleases");

            migrationBuilder.DropTable(
                name: "ProductionOrderEvents");

            migrationBuilder.DropTable(
                name: "ProductionOrderExpenses");

            migrationBuilder.DropTable(
                name: "ProductionLogRows");

            migrationBuilder.DropTable(
                name: "MaterialMovements");

            migrationBuilder.DropTable(
                name: "ProductionOrderMaterials");

            migrationBuilder.DropTable(
                name: "ProductionLogs");

            migrationBuilder.DropTable(
                name: "ProductionOrderStages");

            migrationBuilder.DropTable(
                name: "ProductionOrders");
        }
    }
}
