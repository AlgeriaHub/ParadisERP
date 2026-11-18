using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParadiERP.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class SaleLineCostMissing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CostMissing",
                table: "SaleLines",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Sales already on the books (SAL-02): a sale whose posting carries no cost of goods
            // sold (600) had no cost on any stock-tracked line, since every cost the posting adds
            // is positive. Those lines are flagged. A sale that posted some cost is left alone: the
            // posting is one sum, so which of its lines lacked a cost cannot be told afterwards.
            // Tracked is read as the product is today. Refunds post as "sale" too.
            migrationBuilder.Sql(
                "UPDATE [SaleLines] SET [CostMissing] = 1 " +
                "WHERE [ProductId] IN (SELECT [Id] FROM [Products] WHERE [TrackStock] = 1) " +
                "AND [SaleId] IN (SELECT e.[SourceId] FROM [LedgerEntries] e " +
                "WHERE e.[SourceType] = 'sale' AND e.[IsReversal] = 0 " +
                "AND NOT EXISTS (SELECT 1 FROM [LedgerEntryLines] l " +
                "INNER JOIN [LedgerAccounts] a ON a.[Id] = l.[AccountId] " +
                "WHERE l.[EntryId] = e.[Id] AND a.[Code] = '600'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CostMissing",
                table: "SaleLines");
        }
    }
}
