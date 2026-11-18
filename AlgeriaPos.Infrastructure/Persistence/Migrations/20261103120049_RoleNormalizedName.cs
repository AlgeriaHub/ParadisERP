using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlgeriaPos.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Role names unique as the screens compare them — case and spacing aside — on every provider (AUTH-1
    /// constraint 2, before the roles API is exposed): <c>Roles.NormalizedName</c>, filled from the name,
    /// replaces the unique index on <c>Name</c>. No other data is written.
    /// </summary>
    public partial class RoleNormalizedName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Roles_Name",
                table: "Roles");

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                table: "Roles",
                type: "TEXT",
                maxLength: 80,
                nullable: false,
                defaultValue: "");

            // Filled before the unique index, or every row would share the empty default. Start-up
            // (AuthorizationBootstrap) then rewrites any name whose upper case the database spelled otherwise.
            migrationBuilder.Sql(@"UPDATE ""Roles"" SET ""NormalizedName"" = UPPER(TRIM(""Name""));");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_NormalizedName",
                table: "Roles",
                column: "NormalizedName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Roles_NormalizedName",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                table: "Roles");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);
        }
    }
}
