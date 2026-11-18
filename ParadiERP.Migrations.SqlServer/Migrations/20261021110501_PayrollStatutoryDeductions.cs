using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class PayrollStatutoryDeductions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CalculateStatutoryDeductions",
                table: "PayrollRuns",
                type: "bit",
                nullable: false,
                defaultValue: true);

            // A draft remains editable and is intentionally brought onto the new payslip policy.
            // Locked, approved, paid and reversed documents retain their historical calculation.
            migrationBuilder.Sql("UPDATE [PayrollRuns] SET [CalculateStatutoryDeductions] = 0 WHERE [Status] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CalculateStatutoryDeductions",
                table: "PayrollRuns");
        }
    }
}
