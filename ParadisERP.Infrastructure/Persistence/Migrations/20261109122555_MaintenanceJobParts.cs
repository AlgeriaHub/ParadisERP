using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MaintenanceJobParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MaintenanceJobExpenses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
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
                    table.PrimaryKey("PK_MaintenanceJobExpenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceJobExpenses_MaintenanceJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "MaintenanceJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaintenanceJobExpenses_PaymentVouchers_PaymentVoucherId",
                        column: x => x.PaymentVoucherId,
                        principalTable: "PaymentVouchers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MaintenanceJobParts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DocumentNumber = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Qty = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    UnitCost = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    Value = table.Column<decimal>(type: "TEXT", precision: 28, scale: 10, nullable: false),
                    CostBasis = table.Column<int>(type: "INTEGER", nullable: false),
                    StockMovementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientRequestId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaintenanceJobParts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MaintenanceJobParts_MaintenanceJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "MaintenanceJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaintenanceJobParts_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MaintenanceJobParts_StockMovements_StockMovementId",
                        column: x => x.StockMovementId,
                        principalTable: "StockMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobExpenses_ClientRequestId",
                table: "MaintenanceJobExpenses",
                column: "ClientRequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobExpenses_JobId",
                table: "MaintenanceJobExpenses",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobExpenses_PaymentVoucherId",
                table: "MaintenanceJobExpenses",
                column: "PaymentVoucherId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobParts_ClientRequestId_ProductId",
                table: "MaintenanceJobParts",
                columns: new[] { "ClientRequestId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobParts_DocumentId",
                table: "MaintenanceJobParts",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobParts_DocumentNumber",
                table: "MaintenanceJobParts",
                column: "DocumentNumber");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobParts_JobId_ProductId",
                table: "MaintenanceJobParts",
                columns: new[] { "JobId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobParts_ProductId",
                table: "MaintenanceJobParts",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_MaintenanceJobParts_StockMovementId",
                table: "MaintenanceJobParts",
                column: "StockMovementId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MaintenanceJobExpenses");

            migrationBuilder.DropTable(
                name: "MaintenanceJobParts");
        }
    }
}
