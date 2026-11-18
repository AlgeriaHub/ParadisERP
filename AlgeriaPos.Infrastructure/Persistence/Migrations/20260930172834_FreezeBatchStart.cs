using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FreezeBatchStart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "PeriodStartsOn",
                table: "IssueNotes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PeriodStartsOn",
                table: "IssueNotes");
        }
    }
}
