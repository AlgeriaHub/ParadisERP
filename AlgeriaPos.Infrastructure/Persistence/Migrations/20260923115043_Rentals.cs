using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Rentals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "RentalDepositEnabled",
                table: "Stores",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "RentalOverdueMultiplier",
                table: "Stores",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 1.5m);
            // ⚠ The column default EF generates for a new non-nullable decimal is 0, and 0 is
            // the one value this figure must never hold: the overdue price is the rental price
            // times this multiplier, so every company already using the system would come out
            // of this migration charging nothing at all for a late return. The C# property
            // initialiser (1.5) never reaches the database — it runs when the application makes
            // a row, not when the migration adds a column to rows that already exist.
            //
            // 1.5x is the figure §5.5 gives as the example and the engine's own default, and an
            // admin changes it from the back office. The statement below is belt and braces
            // beside the default: it states the intent for the rows that are actually there.
            //
            // Quoted, because SQLite stores decimal as TEXT: an unquoted 1.5 would be written
            // as a REAL and read back as something EF cannot parse out of that column. The
            // server providers convert the literal on the way in, so one statement serves all
            // three.
            migrationBuilder.Sql("UPDATE Stores SET RentalOverdueMultiplier = '1.5'");


            migrationBuilder.AddColumn<decimal>(
                name: "RentalOverdueMultiplier",
                table: "Products",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RentalContracts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    LockedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SignedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DepositAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentalContracts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RentalContracts_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RentalContracts_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IssueNotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ContractId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IssueNotes_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IssueNotes_RentalContracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "RentalContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RentalCharges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContractId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContractLineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    ReturnNoteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IssueNoteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ExtensionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Qty = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    Days = table.Column<int>(type: "INTEGER", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    IsPosted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentalCharges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RentalCharges_RentalContracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "RentalContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RentalContractLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContractId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProductId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sku = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NameSnapshot = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Qty = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    DurationDays = table.Column<int>(type: "INTEGER", nullable: false),
                    RentUnitPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    DamageUnitPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    OverdueUnitPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    ParentLineId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentalContractLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RentalContractLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RentalContractLines_RentalContractLines_ParentLineId",
                        column: x => x.ParentLineId,
                        principalTable: "RentalContractLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RentalContractLines_RentalContracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "RentalContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReturnNotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ContractId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReturnedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    UnusedRent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    ReliefDebtReduction = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    ReliefPaymentVoucher = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReturnNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReturnNotes_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReturnNotes_RentalContracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "RentalContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContractExtensions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ContractId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContractLineId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ExtraDays = table.Column<int>(type: "INTEGER", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    RaisedOnUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PeriodEndsOnUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsRetroactive = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ApprovedByName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContractExtensions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContractExtensions_RentalContractLines_ContractLineId",
                        column: x => x.ContractLineId,
                        principalTable: "RentalContractLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContractExtensions_RentalContracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "RentalContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "IssueNoteLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    IssueNoteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContractLineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Qty = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IssueNoteLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IssueNoteLines_IssueNotes_IssueNoteId",
                        column: x => x.IssueNoteId,
                        principalTable: "IssueNotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IssueNoteLines_RentalContractLines_ContractLineId",
                        column: x => x.ContractLineId,
                        principalTable: "RentalContractLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReturnNoteLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReturnNoteId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContractLineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    QtyOk = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    QtyPartialDamage = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    MaintenanceValue = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    QtyTotalDamage = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    QtyMissing = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    ReportedOnUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReturnNoteLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReturnNoteLines_RentalContractLines_ContractLineId",
                        column: x => x.ContractLineId,
                        principalTable: "RentalContractLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReturnNoteLines_ReturnNotes_ReturnNoteId",
                        column: x => x.ReturnNoteId,
                        principalTable: "ReturnNotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContractExtensions_ContractId",
                table: "ContractExtensions",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractExtensions_ContractLineId",
                table: "ContractExtensions",
                column: "ContractLineId");

            migrationBuilder.CreateIndex(
                name: "IX_ContractExtensions_IsRetroactive",
                table: "ContractExtensions",
                column: "IsRetroactive");

            migrationBuilder.CreateIndex(
                name: "IX_ContractExtensions_Number",
                table: "ContractExtensions",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IssueNoteLines_ContractLineId",
                table: "IssueNoteLines",
                column: "ContractLineId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueNoteLines_IssueNoteId",
                table: "IssueNoteLines",
                column: "IssueNoteId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueNotes_BranchId",
                table: "IssueNotes",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_IssueNotes_ContractId_IssuedAtUtc",
                table: "IssueNotes",
                columns: new[] { "ContractId", "IssuedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_IssueNotes_Number",
                table: "IssueNotes",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RentalCharges_ContractId_Kind",
                table: "RentalCharges",
                columns: new[] { "ContractId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_RentalCharges_ReturnNoteId",
                table: "RentalCharges",
                column: "ReturnNoteId");

            migrationBuilder.CreateIndex(
                name: "IX_RentalContractLines_ContractId",
                table: "RentalContractLines",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_RentalContractLines_ParentLineId",
                table: "RentalContractLines",
                column: "ParentLineId");

            migrationBuilder.CreateIndex(
                name: "IX_RentalContractLines_ProductId",
                table: "RentalContractLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_RentalContracts_BranchId",
                table: "RentalContracts",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_RentalContracts_CustomerId",
                table: "RentalContracts",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_RentalContracts_Number",
                table: "RentalContracts",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RentalContracts_Status_CustomerId",
                table: "RentalContracts",
                columns: new[] { "Status", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReturnNoteLines_ContractLineId",
                table: "ReturnNoteLines",
                column: "ContractLineId");

            migrationBuilder.CreateIndex(
                name: "IX_ReturnNoteLines_ReturnNoteId",
                table: "ReturnNoteLines",
                column: "ReturnNoteId");

            migrationBuilder.CreateIndex(
                name: "IX_ReturnNotes_BranchId",
                table: "ReturnNotes",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_ReturnNotes_ContractId_ReturnedAtUtc",
                table: "ReturnNotes",
                columns: new[] { "ContractId", "ReturnedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReturnNotes_Number",
                table: "ReturnNotes",
                column: "Number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContractExtensions");

            migrationBuilder.DropTable(
                name: "IssueNoteLines");

            migrationBuilder.DropTable(
                name: "RentalCharges");

            migrationBuilder.DropTable(
                name: "ReturnNoteLines");

            migrationBuilder.DropTable(
                name: "IssueNotes");

            migrationBuilder.DropTable(
                name: "RentalContractLines");

            migrationBuilder.DropTable(
                name: "ReturnNotes");

            migrationBuilder.DropTable(
                name: "RentalContracts");

            migrationBuilder.DropColumn(
                name: "RentalDepositEnabled",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "RentalOverdueMultiplier",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "RentalOverdueMultiplier",
                table: "Products");
        }
    }
}
