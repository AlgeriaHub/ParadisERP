using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuthorizationVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // AUTH-1 Step 3: the user's authorization marker. 0 on every existing row is right — the
            // server's cache of decisions starts empty at every start, so nothing was filed under it.
            // Roles.Version already exists (Step 1) and is bumped from here on by every save.
            migrationBuilder.AddColumn<long>(
                name: "AuthorizationVersion",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AuthorizationVersion",
                table: "Users");
        }
    }
}
