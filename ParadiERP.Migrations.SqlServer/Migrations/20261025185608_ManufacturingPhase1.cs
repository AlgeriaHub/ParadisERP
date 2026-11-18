using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ParadiERP.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class ManufacturingPhase1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BaseUnitId",
                table: "Products",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ManufacturingRole",
                table: "Products",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ManufacturingJobRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    NameFr = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManufacturingJobRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductionLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameFr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ManagerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionLines_Branches_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionLines_Users_ManagerUserId",
                        column: x => x.ManagerUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StageTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameFr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StageTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StageTemplates_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UnitsOfMeasure",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    NameFr = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    Dimension = table.Column<int>(type: "int", nullable: false),
                    FactorToReference = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UnitsOfMeasure", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductionLineEmployees",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AssignedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EndedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    AssignedBy = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionLineEmployees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionLineEmployees_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionLineEmployees_ManufacturingJobRoles_JobRoleId",
                        column: x => x.JobRoleId,
                        principalTable: "ManufacturingJobRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionLineEmployees_ProductionLines_LineId",
                        column: x => x.LineId,
                        principalTable: "ProductionLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionLineMembers",
                columns: table => new
                {
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    AddedBy = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    AddedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionLineMembers", x => new { x.LineId, x.UserId });
                    table.ForeignKey(
                        name: "FK_ProductionLineMembers_ProductionLines_LineId",
                        column: x => x.LineId,
                        principalTable: "ProductionLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionLineMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductionLineProducts",
                columns: table => new
                {
                    LineId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionLineProducts", x => new { x.LineId, x.ProductId });
                    table.ForeignKey(
                        name: "FK_ProductionLineProducts_ProductionLines_LineId",
                        column: x => x.LineId,
                        principalTable: "ProductionLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductionLineProducts_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StageTemplateSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameFr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EstimatedMinutes = table.Column<int>(type: "int", nullable: false),
                    ExpectedWorkers = table.Column<int>(type: "int", nullable: false),
                    IsQualityCheckpoint = table.Column<bool>(type: "bit", nullable: false),
                    QualityNote = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StageTemplateSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StageTemplateSteps_StageTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "StageTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BillsOfMaterials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductionLineId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    BatchQuantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    BatchUnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StageTemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ActivatedBy = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ActivatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RetiredBy = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RetiredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillsOfMaterials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BillsOfMaterials_ProductionLines_ProductionLineId",
                        column: x => x.ProductionLineId,
                        principalTable: "ProductionLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BillsOfMaterials_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BillsOfMaterials_StageTemplates_StageTemplateId",
                        column: x => x.StageTemplateId,
                        principalTable: "StageTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BillsOfMaterials_UnitsOfMeasure_BatchUnitId",
                        column: x => x.BatchUnitId,
                        principalTable: "UnitsOfMeasure",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductUnits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FactorToBase = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    ForPurchase = table.Column<bool>(type: "bit", nullable: false),
                    ForProduction = table.Column<bool>(type: "bit", nullable: false),
                    ForSale = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductUnits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductUnits_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductUnits_UnitsOfMeasure_UnitId",
                        column: x => x.UnitId,
                        principalTable: "UnitsOfMeasure",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StageTemplateStepRoles",
                columns: table => new
                {
                    StepId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StageTemplateStepRoles", x => new { x.StepId, x.JobRoleId });
                    table.ForeignKey(
                        name: "FK_StageTemplateStepRoles_ManufacturingJobRoles_JobRoleId",
                        column: x => x.JobRoleId,
                        principalTable: "ManufacturingJobRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StageTemplateStepRoles_StageTemplateSteps_StepId",
                        column: x => x.StepId,
                        principalTable: "StageTemplateSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BomLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    ComponentProductId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(28,10)", precision: 28, scale: 10, nullable: false),
                    UnitId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WastePercent = table.Column<decimal>(type: "decimal(9,4)", precision: 9, scale: 4, nullable: false),
                    Specification = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BomLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BomLines_BillsOfMaterials_BomId",
                        column: x => x.BomId,
                        principalTable: "BillsOfMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BomLines_Products_ComponentProductId",
                        column: x => x.ComponentProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BomLines_UnitsOfMeasure_UnitId",
                        column: x => x.UnitId,
                        principalTable: "UnitsOfMeasure",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BomStages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    NameAr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameFr = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    NameEn = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EstimatedMinutes = table.Column<int>(type: "int", nullable: false),
                    ExpectedWorkers = table.Column<int>(type: "int", nullable: false),
                    IsQualityCheckpoint = table.Column<bool>(type: "bit", nullable: false),
                    QualityNote = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    IsOutput = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BomStages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BomStages_BillsOfMaterials_BomId",
                        column: x => x.BomId,
                        principalTable: "BillsOfMaterials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BomStageRoles",
                columns: table => new
                {
                    StageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobRoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Count = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BomStageRoles", x => new { x.StageId, x.JobRoleId });
                    table.ForeignKey(
                        name: "FK_BomStageRoles_BomStages_StageId",
                        column: x => x.StageId,
                        principalTable: "BomStages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BomStageRoles_ManufacturingJobRoles_JobRoleId",
                        column: x => x.JobRoleId,
                        principalTable: "ManufacturingJobRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "UnitsOfMeasure",
                columns: new[] { "Id", "Code", "CreatedAtUtc", "Dimension", "FactorToReference", "IsActive", "IsSystem", "NameAr", "NameEn", "NameFr", "UpdatedAtUtc" },
                values: new object[,]
                {
                    { new Guid("0a17e000-0000-4000-8000-000000000001"), "pcs", new DateTime(2026, 10, 25, 0, 0, 0, 0, DateTimeKind.Utc), 0, 1m, true, true, "قطعة", "Piece", "Pièce", new DateTime(2026, 10, 25, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("0a17e000-0000-4000-8000-000000000002"), "kg", new DateTime(2026, 10, 25, 0, 0, 0, 0, DateTimeKind.Utc), 1, 1m, true, true, "كيلوغرام", "Kilogram", "Kilogramme", new DateTime(2026, 10, 25, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("0a17e000-0000-4000-8000-000000000003"), "g", new DateTime(2026, 10, 25, 0, 0, 0, 0, DateTimeKind.Utc), 1, 0.001m, true, true, "غرام", "Gram", "Gramme", new DateTime(2026, 10, 25, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("0a17e000-0000-4000-8000-000000000004"), "m", new DateTime(2026, 10, 25, 0, 0, 0, 0, DateTimeKind.Utc), 2, 1m, true, true, "متر", "Metre", "Mètre", new DateTime(2026, 10, 25, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("0a17e000-0000-4000-8000-000000000005"), "cm", new DateTime(2026, 10, 25, 0, 0, 0, 0, DateTimeKind.Utc), 2, 0.01m, true, true, "سنتيمتر", "Centimetre", "Centimètre", new DateTime(2026, 10, 25, 0, 0, 0, 0, DateTimeKind.Utc) },
                    { new Guid("0a17e000-0000-4000-8000-000000000006"), "l", new DateTime(2026, 10, 25, 0, 0, 0, 0, DateTimeKind.Utc), 3, 1m, true, true, "لتر", "Litre", "Litre", new DateTime(2026, 10, 25, 0, 0, 0, 0, DateTimeKind.Utc) }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Products_BaseUnitId",
                table: "Products",
                column: "BaseUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_Products_ManufacturingRole",
                table: "Products",
                column: "ManufacturingRole");

            migrationBuilder.CreateIndex(
                name: "IX_BillsOfMaterials_BatchUnitId",
                table: "BillsOfMaterials",
                column: "BatchUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_BillsOfMaterials_ProductId_ProductionLineId_Version",
                table: "BillsOfMaterials",
                columns: new[] { "ProductId", "ProductionLineId", "Version" },
                unique: true,
                filter: "[ProductionLineId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BillsOfMaterials_ProductId_Status",
                table: "BillsOfMaterials",
                columns: new[] { "ProductId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BillsOfMaterials_ProductionLineId",
                table: "BillsOfMaterials",
                column: "ProductionLineId");

            migrationBuilder.CreateIndex(
                name: "IX_BillsOfMaterials_StageTemplateId",
                table: "BillsOfMaterials",
                column: "StageTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_BomLines_BomId",
                table: "BomLines",
                column: "BomId");

            migrationBuilder.CreateIndex(
                name: "IX_BomLines_ComponentProductId",
                table: "BomLines",
                column: "ComponentProductId");

            migrationBuilder.CreateIndex(
                name: "IX_BomLines_UnitId",
                table: "BomLines",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_BomStageRoles_JobRoleId",
                table: "BomStageRoles",
                column: "JobRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_BomStages_BomId_Sequence",
                table: "BomStages",
                columns: new[] { "BomId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_ManufacturingJobRoles_Code",
                table: "ManufacturingJobRoles",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLineEmployees_EmployeeId",
                table: "ProductionLineEmployees",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLineEmployees_JobRoleId",
                table: "ProductionLineEmployees",
                column: "JobRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLineEmployees_LineId_EmployeeId",
                table: "ProductionLineEmployees",
                columns: new[] { "LineId", "EmployeeId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLineMembers_UserId",
                table: "ProductionLineMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLineProducts_ProductId",
                table: "ProductionLineProducts",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLines_Code",
                table: "ProductionLines",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLines_LocationId",
                table: "ProductionLines",
                column: "LocationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionLines_ManagerUserId",
                table: "ProductionLines",
                column: "ManagerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_ProductId_UnitId",
                table: "ProductUnits",
                columns: new[] { "ProductId", "UnitId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductUnits_UnitId",
                table: "ProductUnits",
                column: "UnitId");

            migrationBuilder.CreateIndex(
                name: "IX_StageTemplates_Code",
                table: "StageTemplates",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StageTemplates_ProductId",
                table: "StageTemplates",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_StageTemplateStepRoles_JobRoleId",
                table: "StageTemplateStepRoles",
                column: "JobRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_StageTemplateSteps_TemplateId_Sequence",
                table: "StageTemplateSteps",
                columns: new[] { "TemplateId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_UnitsOfMeasure_Code",
                table: "UnitsOfMeasure",
                column: "Code",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Products_UnitsOfMeasure_BaseUnitId",
                table: "Products",
                column: "BaseUnitId",
                principalTable: "UnitsOfMeasure",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Products_UnitsOfMeasure_BaseUnitId",
                table: "Products");

            migrationBuilder.DropTable(
                name: "BomLines");

            migrationBuilder.DropTable(
                name: "BomStageRoles");

            migrationBuilder.DropTable(
                name: "ProductionLineEmployees");

            migrationBuilder.DropTable(
                name: "ProductionLineMembers");

            migrationBuilder.DropTable(
                name: "ProductionLineProducts");

            migrationBuilder.DropTable(
                name: "ProductUnits");

            migrationBuilder.DropTable(
                name: "StageTemplateStepRoles");

            migrationBuilder.DropTable(
                name: "BomStages");

            migrationBuilder.DropTable(
                name: "ManufacturingJobRoles");

            migrationBuilder.DropTable(
                name: "StageTemplateSteps");

            migrationBuilder.DropTable(
                name: "BillsOfMaterials");

            migrationBuilder.DropTable(
                name: "ProductionLines");

            migrationBuilder.DropTable(
                name: "StageTemplates");

            migrationBuilder.DropTable(
                name: "UnitsOfMeasure");

            migrationBuilder.DropIndex(
                name: "IX_Products_BaseUnitId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_ManufacturingRole",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "BaseUnitId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ManufacturingRole",
                table: "Products");
        }
    }
}
