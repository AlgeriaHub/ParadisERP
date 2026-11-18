using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Step one of two: the path columns, beside the logo data URLs and attachment bytes they
    /// replace. Applied through FileStoreUpgrade, which writes the files out before the next step.
    /// </summary>
    public partial class FileStorePaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LogoPath",
                table: "Stores",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StoragePath",
                table: "RentalContractAttachments",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "LogoPath",
                table: "Branches",
                type: "TEXT",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LogoPath",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "StoragePath",
                table: "RentalContractAttachments");

            migrationBuilder.DropColumn(
                name: "LogoPath",
                table: "Branches");
        }
    }
}
