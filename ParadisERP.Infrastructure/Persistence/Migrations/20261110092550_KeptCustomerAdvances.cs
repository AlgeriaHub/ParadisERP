using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class KeptCustomerAdvances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KeptAdvanceIncomeAccount",
                table: "ReceivableSettings",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "KeptAdvances",
                table: "ReceivableSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "IncomeAccountCode",
                table: "CustomerAdvanceApplications",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Kind",
                table: "CustomerAdvanceApplications",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Reason",
                table: "CustomerAdvanceApplications",
                type: "TEXT",
                maxLength: 400,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeptAdvanceIncomeAccount",
                table: "ReceivableSettings");

            migrationBuilder.DropColumn(
                name: "KeptAdvances",
                table: "ReceivableSettings");

            migrationBuilder.DropColumn(
                name: "IncomeAccountCode",
                table: "CustomerAdvanceApplications");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "CustomerAdvanceApplications");

            migrationBuilder.DropColumn(
                name: "Reason",
                table: "CustomerAdvanceApplications");
        }
    }
}
