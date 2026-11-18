using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OvertimeEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OvertimeEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    RuleKey = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Hours = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OvertimeEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OvertimeEntries_Employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "Employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OvertimeEntryChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EmployeeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Action = table.Column<int>(type: "INTEGER", nullable: false),
                    PreviousWorkDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    PreviousRuleKey = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    PreviousHours = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    WorkDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    RuleKey = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Hours = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: true),
                    ChangedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ChangedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OvertimeEntryChanges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OvertimeEntries_EmployeeId_WorkDate",
                table: "OvertimeEntries",
                columns: new[] { "EmployeeId", "WorkDate" });

            migrationBuilder.CreateIndex(
                name: "IX_OvertimeEntryChanges_EntryId_ChangedAtUtc",
                table: "OvertimeEntryChanges",
                columns: new[] { "EntryId", "ChangedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OvertimeEntries");

            migrationBuilder.DropTable(
                name: "OvertimeEntryChanges");
        }
    }
}
