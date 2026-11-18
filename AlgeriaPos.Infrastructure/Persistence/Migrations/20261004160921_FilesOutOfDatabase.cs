using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Step two of two: drops the logo data URLs and attachment bytes, once FileStoreUpgrade has
    /// written every one to the files folder and recorded its path (owner, 2026-10-04).
    /// </summary>
    public partial class FilesOutOfDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LogoDataUrl",
                table: "Stores");

            migrationBuilder.DropColumn(
                name: "Content",
                table: "RentalContractAttachments");

            migrationBuilder.DropColumn(
                name: "LogoDataUrl",
                table: "Branches");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LogoDataUrl",
                table: "Stores",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "Content",
                table: "RentalContractAttachments",
                type: "BLOB",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<string>(
                name: "LogoDataUrl",
                table: "Branches",
                type: "TEXT",
                nullable: true);
        }
    }
}
