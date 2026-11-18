using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Platform.Migrations
{
    /// <inheritdoc />
    public partial class ProvisionedCompanies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProvisionedCompanies",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    HostsJson = table.Column<string>(type: "TEXT", nullable: false),
                    DatabaseProvider = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ProtectedConnectionString = table.Column<string>(type: "TEXT", nullable: false),
                    AdminUsername = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Country = table.Column<string>(type: "TEXT", maxLength: 2, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProvisionedCompanies", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProvisionedCompanies");
        }
    }
}
