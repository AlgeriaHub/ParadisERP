using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TreasuryAndPaymentMethods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TaxMode",
                table: "Sales",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Behavior",
                table: "SalePayments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmedAtUtc",
                table: "SalePayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstrumentBank",
                table: "SalePayments",
                type: "TEXT",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InstrumentDueDate",
                table: "SalePayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstrumentNumber",
                table: "SalePayments",
                type: "TEXT",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MethodKey",
                table: "SalePayments",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "SalePayments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "StatusNote",
                table: "SalePayments",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CashBoxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NameAr = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    NameFr = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    NameEn = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    IsMain = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashBoxes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashBoxes_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PaymentMethodDefs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    NameAr = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    NameFr = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    NameEn = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Behavior = table.Column<int>(type: "INTEGER", nullable: false),
                    RequiresConfirmation = table.Column<bool>(type: "INTEGER", nullable: false),
                    AffectsCashDrawer = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiresReference = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiresInstrumentDetails = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentMethodDefs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CashMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CashBoxId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    RelatedCashBoxId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashMovements_CashBoxes_CashBoxId",
                        column: x => x.CashBoxId,
                        principalTable: "CashBoxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Existing rows predate the idea of a payment that has not arrived yet: under the
            // old model a recorded payment *was* the money. Left at the column defaults every
            // one of them would become Pending and Cash — re-opening settled customer
            // balances and filling the cheques-due screen with historic cash sales. Restate
            // them as what they always were. ConfirmedAtUtc stays null because the real
            // moment was never recorded, and inventing one is worse than admitting that.
            migrationBuilder.Sql(@"
                UPDATE ""SalePayments"" SET
                    ""MethodKey"" = CASE ""Method""
                        WHEN 0 THEN 'cash' WHEN 1 THEN 'card' WHEN 2 THEN 'credit'
                        WHEN 4 THEN 'giftcard' WHEN 5 THEN 'loyalty' ELSE 'cash' END,
                    ""Behavior"" = CASE ""Method""
                        WHEN 0 THEN 0 WHEN 1 THEN 1 WHEN 2 THEN 2
                        WHEN 4 THEN 3 WHEN 5 THEN 4 ELSE 5 END,
                    ""Status"" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_SalePayments_InstrumentDueDate",
                table: "SalePayments",
                column: "InstrumentDueDate");

            migrationBuilder.CreateIndex(
                name: "IX_SalePayments_Status",
                table: "SalePayments",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CashBoxes_BranchId",
                table: "CashBoxes",
                column: "BranchId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_CashBoxId",
                table: "CashMovements",
                column: "CashBoxId");

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_OccurredAtUtc",
                table: "CashMovements",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CashMovements_SourceType_SourceId",
                table: "CashMovements",
                columns: new[] { "SourceType", "SourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentMethodDefs_Key",
                table: "PaymentMethodDefs",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentMethodDefs_Version",
                table: "PaymentMethodDefs",
                column: "Version");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CashMovements");

            migrationBuilder.DropTable(
                name: "PaymentMethodDefs");

            migrationBuilder.DropTable(
                name: "CashBoxes");

            migrationBuilder.DropIndex(
                name: "IX_SalePayments_InstrumentDueDate",
                table: "SalePayments");

            migrationBuilder.DropIndex(
                name: "IX_SalePayments_Status",
                table: "SalePayments");

            migrationBuilder.DropColumn(
                name: "TaxMode",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "Behavior",
                table: "SalePayments");

            migrationBuilder.DropColumn(
                name: "ConfirmedAtUtc",
                table: "SalePayments");

            migrationBuilder.DropColumn(
                name: "InstrumentBank",
                table: "SalePayments");

            migrationBuilder.DropColumn(
                name: "InstrumentDueDate",
                table: "SalePayments");

            migrationBuilder.DropColumn(
                name: "InstrumentNumber",
                table: "SalePayments");

            migrationBuilder.DropColumn(
                name: "MethodKey",
                table: "SalePayments");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "SalePayments");

            migrationBuilder.DropColumn(
                name: "StatusNote",
                table: "SalePayments");
        }
    }
}
