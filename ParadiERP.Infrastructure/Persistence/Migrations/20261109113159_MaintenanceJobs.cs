using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MaintenanceJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaintenanceJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Qty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    ItemReference = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    ContractId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReturnNoteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReturnNoteLineId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Diagnosis = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    RepairedQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    ScrappedQty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    ClientRequestId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    OpenedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    OpenedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    OpenedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CompletedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelledByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CancelledBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CancelReason = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceJobs_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaintenanceJobs_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaintenanceJobs_RentalContracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "RentalContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaintenanceJobs_ReturnNoteLines_ReturnNoteLineId",
                        column: x => x.ReturnNoteLineId,
                        principalTable: "ReturnNoteLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaintenanceJobs_ReturnNotes_ReturnNoteId",
                        column: x => x.ReturnNoteId,
                        principalTable: "ReturnNotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceJobEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceJobEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceJobEvents_MaintenanceJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "MaintenanceJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobEvents_JobId_AtUtc",
                table: "MaintenanceJobEvents",
                columns: new[] { "JobId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobs_BranchId_ProductId_Status",
                table: "MaintenanceJobs",
                columns: new[] { "BranchId", "ProductId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobs_BranchId_Status",
                table: "MaintenanceJobs",
                columns: new[] { "BranchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobs_ClientRequestId",
                table: "MaintenanceJobs",
                column: "ClientRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobs_ContractId",
                table: "MaintenanceJobs",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobs_Number",
                table: "MaintenanceJobs",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobs_ProductId",
                table: "MaintenanceJobs",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobs_ReturnNoteId",
                table: "MaintenanceJobs",
                column: "ReturnNoteId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobs_ReturnNoteLineId",
                table: "MaintenanceJobs",
                column: "ReturnNoteLineId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaintenanceJobEvents");

            migrationBuilder.DropTable(
                name: "MaintenanceJobs");
        }
    }
}
