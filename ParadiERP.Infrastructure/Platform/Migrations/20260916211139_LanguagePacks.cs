using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Infrastructure.Platform.Migrations
{
    /// <inheritdoc />
    public partial class LanguagePacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LanguagePacks",
                columns: table => new
                {
                    Code = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    PackJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LanguagePacks", x => x.Code);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LanguagePacks");
        }
    }
}
