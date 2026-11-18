using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OwnerPaperwork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LogoDataUrl",
                table: "Stores",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RentalDayCutoffHour",
                table: "Stores",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CountReturnDay",
                table: "ReturnNotes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DriverName",
                table: "ReturnNotes",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DriverPhone",
                table: "ReturnNotes",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReturnDayDecidedBy",
                table: "ReturnNotes",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReturnDayReason",
                table: "ReturnNotes",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TruckNumber",
                table: "ReturnNotes",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "SignatoryBirthDate",
                table: "RentalContracts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "SignatoryIdIssuedOn",
                table: "RentalContracts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignatoryIdIssuer",
                table: "RentalContracts",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignatoryIdType",
                table: "RentalContracts",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SiteAddress",
                table: "RentalContracts",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SiteId",
                table: "RentalContracts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SiteName",
                table: "RentalContracts",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TruckNumber",
                table: "RentalContracts",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TruckOwnerPhone",
                table: "RentalContracts",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TruckType",
                table: "RentalContracts",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NameArSnapshot",
                table: "RentalContractLines",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "ReceiptVouchers",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RentalDamageUnitPrice",
                table: "Products",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DriverName",
                table: "IssueNotes",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DriverPhone",
                table: "IssueNotes",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TruckNumber",
                table: "IssueNotes",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "IdDocumentIssuedOn",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdDocumentIssuer",
                table: "Customers",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdDocumentType",
                table: "Customers",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "IdDocumentIssuedOn",
                table: "CustomerRepresentatives",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdDocumentIssuer",
                table: "CustomerRepresentatives",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdDocumentType",
                table: "CustomerRepresentatives",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FiscalIds",
                table: "Branches",
                type: "TEXT",
                maxLength: 2000,
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.CreateTable(
                name: "CustomerSites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerSites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerSites_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentVouchers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PayeeName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    ExpenseCategory = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    ExpenseAccountCode = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    MethodKey = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    InstrumentNumber = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    InstrumentBank = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ContractId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReturnNoteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UnusedDays = table.Column<int>(type: "INTEGER", nullable: true),
                    DeductedFromDebt = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ApprovedByName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PaidAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PaidByName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StatusNote = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentVouchers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentVouchers_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentVouchers_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentVouchers_RentalContracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "RentalContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentVouchers_ReturnNotes_ReturnNoteId",
                        column: x => x.ReturnNoteId,
                        principalTable: "ReturnNotes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RentalContractAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContractId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Length = table.Column<long>(type: "INTEGER", nullable: false),
                    Content = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UploadedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UploadedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentalContractAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RentalContractAttachments_RentalContracts_ContractId",
                        column: x => x.ContractId,
                        principalTable: "RentalContracts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RentalContracts_SiteId",
                table: "RentalContracts",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerSites_CustomerId",
                table: "CustomerSites",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentVouchers_BranchId",
                table: "PaymentVouchers",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentVouchers_ContractId",
                table: "PaymentVouchers",
                column: "ContractId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentVouchers_CustomerId",
                table: "PaymentVouchers",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentVouchers_Number",
                table: "PaymentVouchers",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentVouchers_ReturnNoteId",
                table: "PaymentVouchers",
                column: "ReturnNoteId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentVouchers_Status",
                table: "PaymentVouchers",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_RentalContractAttachments_ContractId",
                table: "RentalContractAttachments",
                column: "ContractId");

            migrationBuilder.AddForeignKey(
                name: "FK_RentalContracts_CustomerSites_SiteId",
                table: "RentalContracts",
                column: "SiteId",
                principalTable: "CustomerSites",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RentalContracts_CustomerSites_SiteId",
                table: "RentalContracts");

            migrationBuilder.DropTable(
                name: "CustomerSites");

            migrationBuilder.DropTable(
                name: "PaymentVouchers");

            migrationBuilder.DropTable(
                name: "RentalContractAttachments");

            migrationBuilder.DropIndex(
                name: "IX_RentalContracts_SiteId",
                table: "RentalContracts");

            migrationBuilder.DropColumn(
                name: "LogoDataUrl",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "RentalDayCutoffHour",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "CountReturnDay",
                table: "ReturnNotes");

            migrationBuilder.DropColumn(
                name: "DriverName",
                table: "ReturnNotes");

            migrationBuilder.DropColumn(
                name: "DriverPhone",
                table: "ReturnNotes");

            migrationBuilder.DropColumn(
                name: "ReturnDayDecidedBy",
                table: "ReturnNotes");

            migrationBuilder.DropColumn(
                name: "ReturnDayReason",
                table: "ReturnNotes");

            migrationBuilder.DropColumn(
                name: "TruckNumber",
                table: "ReturnNotes");

            migrationBuilder.DropColumn(
                name: "SignatoryBirthDate",
                table: "RentalContracts");

            migrationBuilder.DropColumn(
                name: "SignatoryIdIssuedOn",
                table: "RentalContracts");

            migrationBuilder.DropColumn(
                name: "SignatoryIdIssuer",
                table: "RentalContracts");

            migrationBuilder.DropColumn(
                name: "SignatoryIdType",
                table: "RentalContracts");

            migrationBuilder.DropColumn(
                name: "SiteAddress",
                table: "RentalContracts");

            migrationBuilder.DropColumn(
                name: "SiteId",
                table: "RentalContracts");

            migrationBuilder.DropColumn(
                name: "SiteName",
                table: "RentalContracts");

            migrationBuilder.DropColumn(
                name: "TruckNumber",
                table: "RentalContracts");

            migrationBuilder.DropColumn(
                name: "TruckOwnerPhone",
                table: "RentalContracts");

            migrationBuilder.DropColumn(
                name: "TruckType",
                table: "RentalContracts");

            migrationBuilder.DropColumn(
                name: "NameArSnapshot",
                table: "RentalContractLines");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "ReceiptVouchers");

            migrationBuilder.DropColumn(
                name: "RentalDamageUnitPrice",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "DriverName",
                table: "IssueNotes");

            migrationBuilder.DropColumn(
                name: "DriverPhone",
                table: "IssueNotes");

            migrationBuilder.DropColumn(
                name: "TruckNumber",
                table: "IssueNotes");

            migrationBuilder.DropColumn(
                name: "IdDocumentIssuedOn",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "IdDocumentIssuer",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "IdDocumentType",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "IdDocumentIssuedOn",
                table: "CustomerRepresentatives");

            migrationBuilder.DropColumn(
                name: "IdDocumentIssuer",
                table: "CustomerRepresentatives");

            migrationBuilder.DropColumn(
                name: "IdDocumentType",
                table: "CustomerRepresentatives");

            migrationBuilder.DropColumn(
                name: "FiscalIds",
                table: "Branches");
        }
    }
}
