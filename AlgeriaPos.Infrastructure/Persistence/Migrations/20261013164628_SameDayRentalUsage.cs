using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SameDayRentalUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SameDayUsage",
                table: "ReturnNoteLines",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "UsageSelectedAtUtc",
                table: "ReturnNoteLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UsageSelectedByUserId",
                table: "ReturnNoteLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RentalContractLineId",
                table: "InvoiceLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RentalReturnAllocationId",
                table: "InvoiceLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ContractId",
                table: "CreditNotes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReturnNoteId",
                table: "CreditNotes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReturnAllocationId",
                table: "CreditNoteLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ReturnedQty",
                table: "CreditNoteLines",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReturnNoteLineAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReturnNoteLineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IssueNoteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContractLineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Qty = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    Condition = table.Column<int>(type: "INTEGER", nullable: false),
                    ChargeableDays = table.Column<int>(type: "INTEGER", nullable: false),
                    UnusedRentNet = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    ActualRentNet = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    DailyUnitRate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    TaxRate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReturnedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SameDayUsage = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReturnNoteLineAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReturnNoteLineAllocations_RentalContractLines_ContractLineId",
                        column: x => x.ContractLineId,
                        principalTable: "RentalContractLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReturnNoteLineAllocations_ReturnNoteLines_ReturnNoteLineId",
                        column: x => x.ReturnNoteLineId,
                        principalTable: "ReturnNoteLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReturnNoteLineAllocations_ContractLineId",
                table: "ReturnNoteLineAllocations",
                column: "ContractLineId");

            migrationBuilder.CreateIndex(
                name: "IX_ReturnNoteLineAllocations_IssueNoteId",
                table: "ReturnNoteLineAllocations",
                column: "IssueNoteId");

            migrationBuilder.CreateIndex(
                name: "IX_ReturnNoteLineAllocations_ReturnNoteLineId_IssueNoteId_Condition",
                table: "ReturnNoteLineAllocations",
                columns: new[] { "ReturnNoteLineId", "IssueNoteId", "Condition" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReturnNoteLineAllocations");

            migrationBuilder.DropColumn(
                name: "SameDayUsage",
                table: "ReturnNoteLines");

            migrationBuilder.DropColumn(
                name: "UsageSelectedAtUtc",
                table: "ReturnNoteLines");

            migrationBuilder.DropColumn(
                name: "UsageSelectedByUserId",
                table: "ReturnNoteLines");

            migrationBuilder.DropColumn(
                name: "RentalContractLineId",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "RentalReturnAllocationId",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "ContractId",
                table: "CreditNotes");

            migrationBuilder.DropColumn(
                name: "ReturnNoteId",
                table: "CreditNotes");

            migrationBuilder.DropColumn(
                name: "ReturnAllocationId",
                table: "CreditNoteLines");

            migrationBuilder.DropColumn(
                name: "ReturnedQty",
                table: "CreditNoteLines");
        }
    }
}
