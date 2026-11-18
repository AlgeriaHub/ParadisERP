using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class SalesUntaxedGrant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Sell untaxed at the till" (sales.untaxed; TAX-01, finding E#1; owner, 2026-11-02). Every seller
            // could leave a till sale untaxed before the permission existed, so on the day it arrives nobody loses
            // it: the Manager role gets it (the Admin's comes from its baseline at start-up), and so does every
            // cashier on file, on the account itself, where the users screen can untick it. A new cashier starts
            // without it. Applied at start-up before any request is served, so no cached decision predates it.
            migrationBuilder.Sql(
                "INSERT INTO [RolePermissions] ([RoleId], [PermissionKey]) " +
                "SELECT r.[Id], 'sales.untaxed' FROM [Roles] r WHERE r.[Key] = 'manager' " +
                "AND NOT EXISTS (SELECT 1 FROM [RolePermissions] p WHERE p.[RoleId] = r.[Id] AND p.[PermissionKey] = 'sales.untaxed')");
            // Cashiers: on the Cashier role, or not yet moved onto a role (legacy role 0, Cashier).
            migrationBuilder.Sql(
                "INSERT INTO [UserPermissions] ([UserId], [PermissionKey], [GrantedBy], [GrantedAtUtc]) " +
                "SELECT u.[Id], 'sales.untaxed', 'migration', SYSUTCDATETIME() FROM [Users] u " +
                "WHERE u.[IsDeleted] = 0 " +
                "AND (u.[RoleId] IN (SELECT [Id] FROM [Roles] WHERE [Key] = 'cashier') OR (u.[RoleId] IS NULL AND u.[Role] = 0)) " +
                "AND NOT EXISTS (SELECT 1 FROM [UserPermissions] g WHERE g.[UserId] = u.[Id] AND g.[PermissionKey] = 'sales.untaxed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // What this migration gave. The Admin's row is its baseline's, which an older build repairs at start-up.
            migrationBuilder.Sql("DELETE FROM [UserPermissions] WHERE [PermissionKey] = 'sales.untaxed' AND [GrantedBy] = 'migration'");
            migrationBuilder.Sql(
                "DELETE FROM [RolePermissions] WHERE [PermissionKey] = 'sales.untaxed' " +
                "AND [RoleId] IN (SELECT [Id] FROM [Roles] WHERE [Key] = 'manager')");
        }
    }
}
