using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PayrollVouchers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceId",
                table: "PaymentVouchers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                table: "PaymentVouchers",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentVouchers_SourceType_SourceId",
                table: "PaymentVouchers",
                columns: new[] { "SourceType", "SourceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentVouchers_SourceType_SourceId",
                table: "PaymentVouchers");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "PaymentVouchers");

            migrationBuilder.DropColumn(
                name: "SourceType",
                table: "PaymentVouchers");
        }
    }
}
