using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Auth;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Sales;
using Microsoft.Data.Sqlite;

namespace AlgeriaPos.Tests.Security;

/// <summary>
/// A cashier sees and touches one branch — the branch of the terminal the session is bound to
/// (docs/07 §2.1). The branch is derived from the token's device claim, never from a query
/// parameter; a manager sees the branch they are assigned to (P0.1b, owner 2026-10-25), the admin
/// sees everything; an unknown device sees nothing.
///
/// <para>
/// These drive <see cref="BranchScope"/> and <see cref="SalesQueries"/> directly on the
/// TillHarness, the way SyncDeviceBindingTests drive SyncService: the rule is the query, not
/// the route, so it needs no HTTP host.
/// </para>
/// </summary>
public class CashierBranchScopeTests
{
    private static readonly RegionalSettings Money = RegionalPresets.Create(RegionalPresets.DefaultCountry)!;

    /// <summary>One cash sale at each branch, taken by each branch's own till.</summary>
    private static async Task<(SqliteConnection Connection, PosDbContext Db, Guid AlgiersSale, Guid OranSale)> ShopWithADayOfSalesAsync()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        var sync = TillHarness.NewService(db);
        var algiers = TillHarness.Sale("A-0001", 1, payments: TillHarness.Tender("cash", 1000m));
        var oran = TillHarness.Sale("O-0001", 1, payments: TillHarness.Tender("cash", 1000m));
        Assert.Equal(1, (await sync.PushAsync(new PushRequest("C01", [algiers]), "C01", CancellationToken.None)).Accepted);
        Assert.Equal(1, (await sync.PushAsync(new PushRequest("C02", [oran]), "C02", CancellationToken.None)).Accepted);
        db.ChangeTracker.Clear();
        return (connection, db, algiers.Id, oran.Id);
    }

    [Fact]
    public async Task A_cashier_sees_only_the_sales_its_terminal_s_branch_took()
    {
        var (connection, db, algiersSale, _) = await ShopWithADayOfSalesAsync();
        await using var _ = connection;
        await using var __ = db;

        var scope = await BranchScope.ForAsync(TillHarness.Principal(UserRole.Cashier, "C01"), db, CancellationToken.None);
        Assert.Equal(TillHarness.Algiers, scope);

        var rows = await SalesQueries.ListAsync(db, null, null, null, null, null, scope, CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal(algiersSale, row.Id);
        Assert.Equal(TillHarness.Algiers, row.BranchId);
    }

    [Fact]
    public async Task A_cashier_s_branchId_query_parameter_is_overridden_not_honoured()
    {
        var (connection, db, _, _) = await ShopWithADayOfSalesAsync();
        await using var _ = connection;
        await using var __ = db;
        var scope = await BranchScope.ForAsync(TillHarness.Principal(UserRole.Cashier, "C01"), db, CancellationToken.None);

        // The Algiers till asks for Oran's sales by name. The scope is applied after the
        // caller's filters, so the answer is Oran ∩ Algiers — nothing — not Oran's day.
        var rows = await SalesQueries.ListAsync(db, null, null, null, TillHarness.Oran, null, scope, CancellationToken.None);
        Assert.Empty(rows);

        // Asking by the other till's code is the same question and gets the same answer.
        Assert.Empty(await SalesQueries.ListAsync(db, null, null, "C02", null, null, scope, CancellationToken.None));
    }

    [Fact]
    public async Task A_cashier_cannot_open_another_branch_s_sale_by_id()
    {
        var (connection, db, algiersSale, oranSale) = await ShopWithADayOfSalesAsync();
        await using var _ = connection;
        await using var __ = db;
        var scope = await BranchScope.ForAsync(TillHarness.Principal(UserRole.Cashier, "C01"), db, CancellationToken.None);

        // Not found, never forbidden: a 403 would confirm the sale exists (§2.1).
        Assert.Null(await SalesQueries.FindAsync(db, oranSale, scope, CancellationToken.None));

        var own = await SalesQueries.FindAsync(db, algiersSale, scope, CancellationToken.None);
        Assert.NotNull(own);
        Assert.Equal("A-0001", own.Reference);
        Assert.Single(own.Lines);
        Assert.Single(own.Payments);
    }

    [Fact]
    public async Task A_cashier_s_Z_report_names_no_other_branch()
    {
        var (connection, db, _, _) = await ShopWithADayOfSalesAsync();
        await using var _ = connection;
        await using var __ = db;
        var scope = await BranchScope.ForAsync(TillHarness.Principal(UserRole.Cashier, "C01"), db, CancellationToken.None);
        var today = DateTime.UtcNow.Date;

        // No branchId asked for — the request that gives a manager the per-branch comparison.
        var summary = await SalesQueries.SummaryAsync(db, today, today.AddDays(1), null, scope, Money, CancellationToken.None);

        Assert.Equal(1, summary.SaleCount);
        Assert.Equal(1000m, summary.GrossTtc);
        Assert.Empty(summary.BranchBreakdown);

        // Asking for the other branch outright is overridden the same way the list is.
        var oran = await SalesQueries.SummaryAsync(db, today, today.AddDays(1), TillHarness.Oran, scope, Money, CancellationToken.None);
        Assert.Equal(1, oran.SaleCount);
        Assert.Empty(oran.BranchBreakdown);
    }

    [Fact]
    public async Task A_manager_sees_the_branch_they_are_assigned_to_and_the_admin_every_branch()
    {
        var (connection, db, algiersSale, oranSale) = await ShopWithADayOfSalesAsync();
        await using var _ = connection;
        await using var __ = db;
        var today = DateTime.UtcNow.Date;
        var manager = new User { Username = "gerant-oran", FullName = "G", BranchId = TillHarness.Oran, Role = UserRole.Manager, PinHash = "h", PasswordHash = "x" };
        db.Users.Add(manager);
        await db.SaveChangesAsync();

        // Signed in at the Algiers till, and still held to Oran: the assignment decides, never the
        // device — the branch their lists and reports already read (P0.1b).
        var scope = await BranchScope.ForAsync(TillHarness.Principal(UserRole.Manager, "C01", manager.Id), db, CancellationToken.None);
        Assert.Equal(TillHarness.Oran, scope);
        Assert.Equal([oranSale], (await SalesQueries.ListAsync(db, null, null, null, null, null, scope, CancellationToken.None)).Select(s => s.Id));
        Assert.Null(await SalesQueries.FindAsync(db, algiersSale, scope, CancellationToken.None));
        Assert.NotNull(await SalesQueries.FindAsync(db, oranSale, scope, CancellationToken.None));

        // The admin reads every branch.
        var all = await BranchScope.ForAsync(TillHarness.Principal(UserRole.Admin, "C01"), db, CancellationToken.None);
        Assert.Null(all);
        Assert.Equal(2, (await SalesQueries.ListAsync(db, null, null, null, null, null, all, CancellationToken.None)).Count);
        var summary = await SalesQueries.SummaryAsync(db, today, today.AddDays(1), null, all, Money, CancellationToken.None);
        Assert.Equal(2, summary.SaleCount);
        Assert.Equal(2, summary.BranchBreakdown.Count);
        Assert.Contains(summary.BranchBreakdown, b => b.BranchId == TillHarness.Oran && b.BranchName == "Oran");
    }

    [Fact]
    public async Task An_unknown_device_claim_scopes_to_nothing_rather_than_everything()
    {
        var (connection, db, algiersSale, _) = await ShopWithADayOfSalesAsync();
        await using var _ = connection;
        await using var __ = db;
        var today = DateTime.UtcNow.Date;

        // Token validation already refuses an unknown device; this is the defence behind it.
        foreach (var device in new[] { "ZZ9", null })
        {
            var scope = await BranchScope.ForAsync(TillHarness.Principal(UserRole.Cashier, device), db, CancellationToken.None);
            Assert.Equal(Guid.Empty, scope);

            Assert.Empty(await SalesQueries.ListAsync(db, null, null, null, null, null, scope, CancellationToken.None));
            Assert.Null(await SalesQueries.FindAsync(db, algiersSale, scope, CancellationToken.None));
            var summary = await SalesQueries.SummaryAsync(db, today, today.AddDays(1), null, scope, Money, CancellationToken.None);
            Assert.Equal(0, summary.SaleCount);
            Assert.Empty(summary.BranchBreakdown);
        }
    }

    [Fact]
    public async Task A_cashier_cannot_send_another_branch_s_receipt()
    {
        var (connection, db, algiersSale, oranSale) = await ShopWithADayOfSalesAsync();
        await using var _ = connection;
        await using var __ = db;
        var scope = await BranchScope.ForAsync(TillHarness.Principal(UserRole.Cashier, "C01"), db, CancellationToken.None);

        // The send-receipt route loads the sale through this and answers 404 on null — the
        // same rule as the read by id, applied to the one write a till may aim at any sale.
        Assert.Null(await SalesQueries.ForReceiptAsync(db, oranSale, scope, CancellationToken.None));

        var own = await SalesQueries.ForReceiptAsync(db, algiersSale, scope, CancellationToken.None);
        Assert.NotNull(own);
        Assert.NotEmpty(own.Lines);
        Assert.NotEmpty(own.Payments);

        // A manager re-sends any branch's receipt, as before.
        Assert.NotNull(await SalesQueries.ForReceiptAsync(db, oranSale, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_manager_without_the_stock_permission_reads_stock_for_the_branch_they_are_signed_in_at()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var oranManager = new User { Username = "m.oran", FullName = "Manager Oran", Role = UserRole.Manager, BranchId = TillHarness.Oran };
        var oranCashier = new User { Username = "c.oran", FullName = "Caissier Oran", Role = UserRole.Cashier, BranchId = TillHarness.Oran };
        db.Users.AddRange(oranManager, oranCashier);
        await db.SaveChangesAsync();

        // The catalog stock routes confine a caller without ViewOtherBranchStock to their device's
        // branch; the permission opens every branch.
        //
        // A manager's own User.BranchId is ignored here exactly as ForAsync ignores it: the column
        // binds cashiers, and the users tab promises a manager is not held to a row left over from
        // before a promotion. Signed in at the Algiers till, this Oran-stamped manager reads
        // Algiers — the branch they are actually standing in — not Oran.
        Assert.Equal(TillHarness.Algiers, await BranchScope.ForStockAsync(
            TillHarness.Principal(UserRole.Manager, "C01", oranManager.Id), db, CancellationToken.None));
        Assert.Equal(TillHarness.Algiers, await BranchScope.ForStockAsync(
            TillHarness.Principal(UserRole.Manager, "C01"), db, CancellationToken.None));
        Assert.Null(await BranchScope.ForStockAsync(
            TillHarness.Principal(UserRole.Manager, "C01", oranManager.Id, Permissions.ViewOtherBranchStock), db, CancellationToken.None));
        Assert.Equal(Guid.Empty, await BranchScope.ForStockAsync(
            TillHarness.Principal(UserRole.Manager, null), db, CancellationToken.None));

        // The binding still answers first for the role it was written for. A bound cashier who
        // somehow signed in elsewhere reads their own branch's stock and no other.
        Assert.Equal(TillHarness.Oran, await BranchScope.ForStockAsync(
            TillHarness.Principal(UserRole.Cashier, "C01", oranCashier.Id), db, CancellationToken.None));
    }
}
