using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExtensionAsNewContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsCarryOver",
                table: "ReturnNotes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ExtendsContractId",
                table: "RentalContracts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsCarryOver",
                table: "IssueNotes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsCarryOver",
                table: "ReturnNotes");

            migrationBuilder.DropColumn(
                name: "ExtendsContractId",
                table: "RentalContracts");

            migrationBuilder.DropColumn(
                name: "IsCarryOver",
                table: "IssueNotes");
        }
    }
}
