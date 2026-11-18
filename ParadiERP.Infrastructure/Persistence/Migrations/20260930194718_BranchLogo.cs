using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BranchLogo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LogoDataUrl",
                table: "Branches",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LogoDataUrl",
                table: "Branches");
        }
    }
}
