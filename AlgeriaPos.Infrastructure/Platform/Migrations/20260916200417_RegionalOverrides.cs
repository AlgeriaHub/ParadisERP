using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Platform.Migrations
{
    /// <inheritdoc />
    public partial class RegionalOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RegionalOverrides",
                columns: table => new
                {
                    CompanyId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegionalOverrides", x => x.CompanyId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RegionalOverrides");
        }
    }
}
