using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadisERP.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StockStatesAndAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Permissions",
                table: "Users",
                type: "TEXT",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MaintenanceQty",
                table: "ProductStocks",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "OnRentQty",
                table: "ProductStocks",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ScrapQty",
                table: "ProductStocks",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AssetUnitValue",
                table: "Products",
                type: "TEXT",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<bool>(
                name: "IsRentable",
                table: "Products",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsOpenForOperation",
                table: "Branches",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Every branch that exists at this point has been trading for as long as the
            // company has used the system. Left at the column default they would all come
            // back as "still in setup", which is exactly the state that accepts opening
            // balances — turning the one documented exception into an open back door past
            // the warehouse rule on day one.
            migrationBuilder.Sql("UPDATE Branches SET IsOpenForOperation = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Permissions",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "MaintenanceQty",
                table: "ProductStocks");

            migrationBuilder.DropColumn(
                name: "OnRentQty",
                table: "ProductStocks");

            migrationBuilder.DropColumn(
                name: "ScrapQty",
                table: "ProductStocks");

            migrationBuilder.DropColumn(
                name: "AssetUnitValue",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "IsRentable",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "IsOpenForOperation",
                table: "Branches");
        }
    }
}
