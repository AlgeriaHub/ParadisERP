using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PayrollAndDocumentHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Three, hand-written over the scaffolder's 0. EF takes a migration default from the
            // CLR default of the type, not from the property initialiser, and 0 here would mean
            // "this company may never change a printed wording again" on every row that predates
            // the column. Three is the figure the owner named and the value the entity starts at
            // (Store.DefaultDocumentTextChangesPerYear); 0 stays a legitimate setting he can
            // choose deliberately, which is also why the model carries no HasDefaultValue — it
            // would make a stored 0 unreachable through EF.
            migrationBuilder.AddColumn<int>(
                name: "DocumentTextChangesPerYear",
                table: "Stores",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.CreateTable(
                name: "DocumentTextChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    ChangeYear = table.Column<int>(type: "INTEGER", nullable: false),
                    ChangedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ChangedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PreviousTexts = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentTextChanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Employees",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    FullName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NationalId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    SocialSecurityNumber = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    BirthDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Address = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobTitle = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    HiredOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    LeftOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    PayBasis = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseRate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    PayoutMethod = table.Column<int>(type: "INTEGER", nullable: false),
                    BankAccount = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    AttendanceModeOverride = table.Column<int>(type: "INTEGER", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Employees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Employees_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PayrollRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PeriodYear = table.Column<int>(type: "INTEGER", nullable: false),
                    PeriodMonth = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    SettingsSnapshotJson = table.Column<string>(type: "TEXT", nullable: true),
                    CurrencyDecimals = table.Column<int>(type: "INTEGER", nullable: false),
                    HeadCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Gross = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    SocialBase = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    SocialEmployee = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    SocialEmployer = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    TaxBase = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    IncomeTax = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    EmployerLevies = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    AdvancesRecovered = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    OtherDeductions = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    Net = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    IsProvisional = table.Column<bool>(type: "INTEGER", nullable: false),
                    ComputedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LockedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ApprovedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PaidAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LedgerEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReversalEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReversedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReversedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ReversalReason = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollRuns_Branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PayrollStatutoryPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountCode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    PaidAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PayoutMethod = table.Column<int>(type: "INTEGER", nullable: false),
                    CashBoxId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PeriodYear = table.Column<int>(type: "INTEGER", nullable: true),
                    PeriodMonth = table.Column<int>(type: "INTEGER", nullable: true),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LedgerEntryId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollStatutoryPayments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceDays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    HoursWorked = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceDays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceDays_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceDeclarations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PeriodYear = table.Column<int>(type: "INTEGER", nullable: false),
                    PeriodMonth = table.Column<int>(type: "INTEGER", nullable: false),
                    DaysPresent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    PaidLeaveDays = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    UnpaidAbsenceDays = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceDeclarations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceDeclarations_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClockEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClockEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClockEvents_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmployeeAllowances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AllowanceKey = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Value = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmployeeAllowances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmployeeAllowances_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WageAdvances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    GrantedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PayoutMethod = table.Column<int>(type: "INTEGER", nullable: false),
                    CashBoxId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Recovered = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RecoveredInRunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WageAdvances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WageAdvances_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PayrollAdjustments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    InSocialBase = table.Column<bool>(type: "INTEGER", nullable: false),
                    InTaxBase = table.Column<bool>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollAdjustments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollAdjustments_PayrollRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "PayrollRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PayrollRunLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeNumberSnapshot = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    EmployeeNameSnapshot = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SocialNumberSnapshot = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    BranchIdSnapshot = table.Column<Guid>(type: "TEXT", nullable: false),
                    PayBasis = table.Column<int>(type: "INTEGER", nullable: false),
                    BaseRate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    DaysPresent = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    PaidLeaveDays = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    UnpaidAbsenceDays = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    AttendanceSource = table.Column<int>(type: "INTEGER", nullable: false),
                    Gross = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    SocialBase = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    SocialEmployee = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    SocialEmployer = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    TaxBase = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    IncomeTax = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    AdvanceRecovered = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    OtherDeductions = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    Net = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    PaidAmount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    WagePaymentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    WarningsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollRunLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollRunLines_PayrollRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "PayrollRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WagePayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BranchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PaidAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PayoutMethod = table.Column<int>(type: "INTEGER", nullable: false),
                    CashBoxId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 400, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LedgerEntryId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WagePayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WagePayments_PayrollRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "PayrollRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceDayOvertimes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DayId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleKey = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Hours = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceDayOvertimes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceDayOvertimes_AttendanceDays_DayId",
                        column: x => x.DayId,
                        principalTable: "AttendanceDays",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AttendanceDeclarationOvertimes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeclarationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleKey = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Hours = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttendanceDeclarationOvertimes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AttendanceDeclarationOvertimes_AttendanceDeclarations_DeclarationId",
                        column: x => x.DeclarationId,
                        principalTable: "AttendanceDeclarations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PayrollRunLineElements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Qty = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    Rate = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    InSocialBase = table.Column<bool>(type: "INTEGER", nullable: false),
                    InTaxBase = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayrollRunLineElements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PayrollRunLineElements_PayrollRunLines_LineId",
                        column: x => x.LineId,
                        principalTable: "PayrollRunLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WagePaymentLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WagePaymentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunLineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WagePaymentLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WagePaymentLines_PayrollRunLines_RunLineId",
                        column: x => x.RunLineId,
                        principalTable: "PayrollRunLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WagePaymentLines_WagePayments_WagePaymentId",
                        column: x => x.WagePaymentId,
                        principalTable: "WagePayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceDayOvertimes_DayId_RuleKey",
                table: "AttendanceDayOvertimes",
                columns: new[] { "DayId", "RuleKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceDays_EmployeeId_WorkDate",
                table: "AttendanceDays",
                columns: new[] { "EmployeeId", "WorkDate" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceDeclarationOvertimes_DeclarationId_RuleKey",
                table: "AttendanceDeclarationOvertimes",
                columns: new[] { "DeclarationId", "RuleKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AttendanceDeclarations_EmployeeId_PeriodYear_PeriodMonth",
                table: "AttendanceDeclarations",
                columns: new[] { "EmployeeId", "PeriodYear", "PeriodMonth" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClockEvents_EmployeeId_AtUtc",
                table: "ClockEvents",
                columns: new[] { "EmployeeId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTextChanges_Kind_ChangeYear",
                table: "DocumentTextChanges",
                columns: new[] { "Kind", "ChangeYear" });

            migrationBuilder.CreateIndex(
                name: "IX_EmployeeAllowances_EmployeeId_AllowanceKey",
                table: "EmployeeAllowances",
                columns: new[] { "EmployeeId", "AllowanceKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Employees_BranchId_IsActive",
                table: "Employees",
                columns: new[] { "BranchId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Employees_FullName",
                table: "Employees",
                column: "FullName");

            migrationBuilder.CreateIndex(
                name: "IX_Employees_Number",
                table: "Employees",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayrollAdjustments_RunId_EmployeeId",
                table: "PayrollAdjustments",
                columns: new[] { "RunId", "EmployeeId" });

            migrationBuilder.CreateIndex(
                name: "IX_PayrollRunLineElements_LineId_SortOrder",
                table: "PayrollRunLineElements",
                columns: new[] { "LineId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_PayrollRunLines_EmployeeId",
                table: "PayrollRunLines",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollRunLines_RunId_EmployeeId",
                table: "PayrollRunLines",
                columns: new[] { "RunId", "EmployeeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayrollRuns_BranchId_PeriodYear_PeriodMonth",
                table: "PayrollRuns",
                columns: new[] { "BranchId", "PeriodYear", "PeriodMonth" });

            migrationBuilder.CreateIndex(
                name: "IX_PayrollRuns_Number",
                table: "PayrollRuns",
                column: "Number",
                unique: true);

            // The one deliberate difference between the two provider sets. One live run per branch
            // and month, over non-Reversed runs only (Status <> 4): a reversed run keeps the links
            // to what was posted and what undid it, and a plain unique index would let that row
            // block the month from ever being run again. SQLite has partial indexes and gets the
            // constraint; MySQL 8 has none, so the MySQL set carries the plain index above and the
            // rule stands on the code check alone. Either way the check inside BeginWriteScopeAsync
            // is the enforcement — this index is a second pair of hands, not the rule.
            migrationBuilder.CreateIndex(
                name: "UX_PayrollRuns_LiveBranchPeriod",
                table: "PayrollRuns",
                columns: new[] { "BranchId", "PeriodYear", "PeriodMonth" },
                unique: true,
                filter: "\"Status\" <> 4");

            migrationBuilder.CreateIndex(
                name: "IX_PayrollStatutoryPayments_BranchId_AccountCode_PaidAtUtc",
                table: "PayrollStatutoryPayments",
                columns: new[] { "BranchId", "AccountCode", "PaidAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WageAdvances_EmployeeId_Status",
                table: "WageAdvances",
                columns: new[] { "EmployeeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WageAdvances_Number",
                table: "WageAdvances",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WagePaymentLines_RunLineId",
                table: "WagePaymentLines",
                column: "RunLineId");

            migrationBuilder.CreateIndex(
                name: "IX_WagePaymentLines_WagePaymentId",
                table: "WagePaymentLines",
                column: "WagePaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_WagePayments_Number",
                table: "WagePayments",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WagePayments_RunId_PaidAtUtc",
                table: "WagePayments",
                columns: new[] { "RunId", "PaidAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AttendanceDayOvertimes");

            migrationBuilder.DropTable(
                name: "AttendanceDeclarationOvertimes");

            migrationBuilder.DropTable(
                name: "ClockEvents");

            migrationBuilder.DropTable(
                name: "DocumentTextChanges");

            migrationBuilder.DropTable(
                name: "EmployeeAllowances");

            migrationBuilder.DropTable(
                name: "PayrollAdjustments");

            migrationBuilder.DropTable(
                name: "PayrollRunLineElements");

            migrationBuilder.DropTable(
                name: "PayrollStatutoryPayments");

            migrationBuilder.DropTable(
                name: "WageAdvances");

            migrationBuilder.DropTable(
                name: "WagePaymentLines");

            migrationBuilder.DropTable(
                name: "AttendanceDays");

            migrationBuilder.DropTable(
                name: "AttendanceDeclarations");

            migrationBuilder.DropTable(
                name: "PayrollRunLines");

            migrationBuilder.DropTable(
                name: "WagePayments");

            migrationBuilder.DropTable(
                name: "Employees");

            migrationBuilder.DropTable(
                name: "PayrollRuns");

            migrationBuilder.DropColumn(
                name: "DocumentTextChangesPerYear",
                table: "Stores");
        }
    }
}
