using System.Security.Claims;
using System.Text.Json;
using ParadiERP.API.Endpoints;
using ParadiERP.Application.Files;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Rentals;
using ParadiERP.Tests.Files;
using ParadiERP.Tests.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Tests.Paging;

/// <summary>
/// "I need to paginate in backend, not frontend" (owner, 2026-11-17), for the inventory and rentals lists:
/// stock transfers, the item status report and its breakdown's two long lists, the rental contracts and the
/// items on a rental figure of their own. Each is asked a page at a time — ten rows to begin with — its
/// filters run on the server over every row, and its figures (totals, tiles, a running balance) are the
/// server's over all of them, equal to what the list route answers for the same filters.
/// <para>
/// Called through the real routes the way Kestrel would once authorisation has passed (BranchLockTests'
/// harness: the group's filters — the caller's-branch rewrite among them — the binding and the handler):
/// the transfers and the report through their modules as mapped, the rentals' two routes on a group shaped
/// like <c>/api/rentals</c> (the rentals module needs services that harness does not build).
/// </para>
/// </summary>
public class InventoryPageTests
{
    private static readonly Guid Algiers = TillHarness.Algiers;
    private static readonly Guid Oran = TillHarness.Oran;
    private static readonly Guid Pipe = TillHarness.Pipe;
    private static readonly Guid Blida = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private const string Transfers = "/api/catalog/stock/transfers";
    private const string Items = "/api/reports/inventory/items";

    private static readonly Action<IEndpointRouteBuilder> TransferRoutes = static app => app.MapStockTransferEndpoints();
    private static readonly Action<IEndpointRouteBuilder> ReportRoutes = static app => app.MapReportEndpoints();
    private static readonly Action<IEndpointRouteBuilder> RentalRoutes = static app =>
    {
        var group = app.MapGroup("/api/rentals").AtCallersBranch();
        InventoryPageRoutes.MapRentalContractsPage(group);
        InventoryPageRoutes.MapRentalItemFiguresPage(group);
    };

    private sealed class Algeria : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => new()
        {
            CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers",
        };
    }

    private sealed record Harness(Microsoft.Data.Sqlite.SqliteConnection Connection, PosDbContext Db, IFileStore Files) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }

        public async Task<JsonElement> GetAsync(Action<IEndpointRouteBuilder> map, string pattern, ClaimsPrincipal caller,
            string query = "", params (string Key, string Value)[] route)
        {
            var (status, body) = await BranchLockTests.InvokeAsync(Db, Files, map, "GET", pattern, caller, query, route);
            Assert.True(status == 200, $"{pattern}{query} → {status}: {body}");
            return JsonDocument.Parse(body).RootElement.Clone();
        }
    }

    private static async Task<Harness> NewAsync()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        return new Harness(connection, db, TestFiles.Fresh());
    }

    private static string[] Strings(JsonElement page, string field) =>
        page.GetProperty("items").EnumerateArray().Select(r => r.GetProperty(field).GetString()!).ToArray();

    private static string[] ListStrings(JsonElement list, string field) =>
        list.EnumerateArray().Select(r => r.GetProperty(field).GetString()!).ToArray();

    private static decimal[] Balances(JsonElement rows) =>
        rows.EnumerateArray().Select(r => r.GetProperty("balance").GetDecimal()).ToArray();

    // ---------- stock transfers ----------

    /// <summary>
    /// TRF-000001…TRF-000025, one a day from 1 October: a multiple of five goes Oran → Blida, an odd one
    /// Algiers → Oran, an even one Oran → Algiers. 21–23 received, 24 cancelled, 25 partly received with a
    /// shortfall reported and not answered; the rest dispatched.
    /// </summary>
    private static async Task SeedTransfersAsync(PosDbContext db)
    {
        db.Branches.Add(new Branch { Id = Blida, NameFr = "Blida", IsOpenForOperation = true });
        for (var i = 1; i <= 25; i++)
        {
            var (from, to) = i % 5 == 0 ? (Oran, Blida) : i % 2 == 1 ? (Algiers, Oran) : (Oran, Algiers);
            var status = i switch
            {
                25 => StockTransferStatus.PartlyReceived,
                21 or 22 or 23 => StockTransferStatus.Received,
                24 => StockTransferStatus.Cancelled,
                _ => StockTransferStatus.Dispatched,
            };
            var line = new StockTransferLine
            {
                ProductId = Pipe, Sku = "PIPE-90", NameFr = "Tube 90", QtyDispatched = 2m,
                QtyReceived = status == StockTransferStatus.Received ? 2m : i == 25 ? 1m : 0m,
                QtyReturned = status == StockTransferStatus.Cancelled ? 2m : 0m,
                QtyShortPending = i == 25 ? 1m : 0m, QtyShortReported = i == 25 ? 1m : 0m,
            };
            db.StockTransfers.Add(new StockTransfer
            {
                Number = $"TRF-{i:000000}", Status = status, FromBranchId = from, ToBranchId = to,
                DispatchedAtUtc = new DateTime(2026, 10, i, 8, 0, 0, DateTimeKind.Utc), Lines = [line],
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Transfers_ten_newest_first_the_total_and_a_page_past_the_end()
    {
        await using var h = await NewAsync();
        await SeedTransfersAsync(h.Db);
        var (_, admin) = await BranchLockTests.UserAsync(h.Db, UserRole.Admin, Algiers);

        var first = await h.GetAsync(TransferRoutes, Transfers + "/page", admin);
        Assert.Equal(25, first.GetProperty("total").GetInt32());
        Assert.Equal(10, first.GetProperty("take").GetInt32());
        Assert.Equal(Enumerable.Range(16, 10).Reverse().Select(i => $"TRF-{i:000000}"), Strings(first, "number"));

        var third = await h.GetAsync(TransferRoutes, Transfers + "/page", admin, "?skip=20&take=10");
        Assert.Equal(["TRF-000005", "TRF-000004", "TRF-000003", "TRF-000002", "TRF-000001"], Strings(third, "number"));

        // A page past the end, after a filter shrank the list, is answered with the last page.
        var past = await h.GetAsync(TransferRoutes, Transfers + "/page", admin, "?skip=90&take=20");
        Assert.Equal(20, past.GetProperty("skip").GetInt32());
        Assert.Equal(5, past.GetProperty("items").GetArrayLength());

        // Never more than a hundred rows at once; and the rows are the list route's, in its order.
        var all = await h.GetAsync(TransferRoutes, Transfers + "/page", admin, "?take=5000");
        Assert.Equal(100, all.GetProperty("take").GetInt32());
        Assert.Equal(ListStrings(await h.GetAsync(TransferRoutes, Transfers, admin), "number"), Strings(all, "number"));
    }

    [Theory]
    [InlineData("?status=open", 21)]
    [InlineData("?status=shortfall", 1)]
    [InlineData("?status=Received", 3)]
    [InlineData("?branchId=11111111-1111-1111-1111-111111111111", 20)]
    // As the list route reads them: from the instant sent, to the instant sent (the 23rd itself is out).
    [InlineData("?from=2026-10-20&to=2026-10-23", 3)]
    public async Task Transfers_filter_on_the_server_as_the_list_route_does(string query, int expected)
    {
        await using var h = await NewAsync();
        await SeedTransfersAsync(h.Db);
        var (_, admin) = await BranchLockTests.UserAsync(h.Db, UserRole.Admin, Algiers);

        var page = await h.GetAsync(TransferRoutes, Transfers + "/page", admin, query + "&take=100");
        Assert.Equal(expected, page.GetProperty("total").GetInt32());
        Assert.Equal(ListStrings(await h.GetAsync(TransferRoutes, Transfers, admin, query), "number"), Strings(page, "number"));
    }

    [Fact]
    public async Task Transfers_sent_or_received_are_read_against_the_branch_in_view()
    {
        await using var h = await NewAsync();
        await SeedTransfersAsync(h.Db);
        var (_, admin) = await BranchLockTests.UserAsync(h.Db, UserRole.Admin, Algiers);

        var sent = await h.GetAsync(TransferRoutes, Transfers + "/page", admin, $"?branchId={Algiers}&direction=out&take=100");
        Assert.Equal(10, sent.GetProperty("total").GetInt32());
        Assert.All(sent.GetProperty("items").EnumerateArray(), t => Assert.Equal(Algiers, t.GetProperty("fromBranchId").GetGuid()));

        var received = await h.GetAsync(TransferRoutes, Transfers + "/page", admin, $"?branchId={Algiers}&direction=in&take=100");
        Assert.Equal(10, received.GetProperty("total").GetInt32());
        Assert.All(received.GetProperty("items").EnumerateArray(), t => Assert.Equal(Algiers, t.GetProperty("toBranchId").GetGuid()));

        // No branch in view, no direction to read: every transfer.
        Assert.Equal(25, (await h.GetAsync(TransferRoutes, Transfers + "/page", admin, "?direction=out")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_branch_user_pages_their_own_branch_s_transfers_whatever_branch_they_name()
    {
        await using var h = await NewAsync();
        await SeedTransfersAsync(h.Db);
        var (_, manager) = await BranchLockTests.UserAsync(h.Db, UserRole.Manager, Algiers);

        var page = await h.GetAsync(TransferRoutes, Transfers + "/page", manager, $"?branchId={Oran}&take=100");
        Assert.Equal(20, page.GetProperty("total").GetInt32());
        Assert.All(page.GetProperty("items").EnumerateArray(), t =>
            Assert.Contains(Algiers, new[] { t.GetProperty("fromBranchId").GetGuid(), t.GetProperty("toBranchId").GetGuid() }));

        // "Received" is read at the user's own branch, not at the one they named.
        var received = await h.GetAsync(TransferRoutes, Transfers + "/page", manager, $"?branchId={Oran}&direction=in&take=100");
        Assert.Equal(10, received.GetProperty("total").GetInt32());
        Assert.All(received.GetProperty("items").EnumerateArray(), t => Assert.Equal(Algiers, t.GetProperty("toBranchId").GetGuid()));
    }

    // ---------- the item status report ----------

    /// <summary>ITM-01…ITM-11: i at Algiers and one at Oran each, beside the harness's pipe (ten at each).</summary>
    private static async Task SeedItemsAsync(PosDbContext db)
    {
        for (var i = 1; i <= 11; i++)
        {
            var id = Guid.NewGuid();
            db.Products.Add(new Product { Id = id, Sku = $"ITM-{i:00}", NameFr = $"Article {i}", Price = 100m, TrackStock = true });
            db.ProductStocks.Add(new ProductStock { ProductId = id, BranchId = Algiers, StockQty = i });
            db.ProductStocks.Add(new ProductStock { ProductId = id, BranchId = Oran, StockQty = 1 });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Item_status_pages_the_report_with_the_totals_row_over_every_row()
    {
        await using var h = await NewAsync();
        await SeedItemsAsync(h.Db);
        var (_, admin) = await BranchLockTests.UserAsync(h.Db, UserRole.Admin, Algiers);

        var report = await h.GetAsync(ReportRoutes, Items, admin);
        var first = await h.GetAsync(ReportRoutes, Items + "/page", admin);
        Assert.Equal(12, first.GetProperty("total").GetInt32());
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"ITM-{i:00}"), Strings(first, "sku"));
        var summary = first.GetProperty("summary");
        // The totals row is the report's, over every row: 1+…+11 at Algiers, 11 at Oran, 20 pipes.
        Assert.Equal(97m, summary.GetProperty("totals").GetProperty("available").GetDecimal());
        Assert.Equal(report.GetProperty("totals").GetRawText(), summary.GetProperty("totals").GetRawText());
        Assert.Equal("company", summary.GetProperty("scope").GetProperty("kind").GetString());
        // Every row's product, so "select all" selects the whole report and not the page.
        Assert.Equal(
            report.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("productId").GetGuid()),
            summary.GetProperty("productIds").EnumerateArray().Select(p => p.GetGuid()));

        var second = await h.GetAsync(ReportRoutes, Items + "/page", admin, "?skip=10&take=10");
        Assert.Equal(["ITM-11", "PIPE-90"], Strings(second, "sku"));

        var found = await h.GetAsync(ReportRoutes, Items + "/page", admin, "?q=ITM-1");
        Assert.Equal(["ITM-10", "ITM-11"], Strings(found, "sku"));
        Assert.Equal(23m, found.GetProperty("summary").GetProperty("totals").GetProperty("available").GetDecimal());
    }

    [Fact]
    public async Task A_branch_user_s_report_is_their_own_branch_whatever_branch_they_name()
    {
        await using var h = await NewAsync();
        await SeedItemsAsync(h.Db);
        var (_, manager) = await BranchLockTests.UserAsync(h.Db, UserRole.Manager, Oran);

        var page = await h.GetAsync(ReportRoutes, Items + "/page", manager, $"?branchId={Algiers}");
        var summary = page.GetProperty("summary");
        Assert.Equal("own", summary.GetProperty("scope").GetProperty("kind").GetString());
        Assert.Equal(21m, summary.GetProperty("totals").GetProperty("available").GetDecimal());
    }

    /// <summary>Thirteen receipts of one pipe at Algiers, one a day from 1 October: the balance runs 1…13.</summary>
    private static async Task SeedMovementsAsync(PosDbContext db)
    {
        for (var day = 1; day <= 13; day++)
            db.StockMovements.Add(new StockMovement
            {
                ProductId = Pipe, BranchId = Algiers, QtyDelta = 1m, BalanceAfter = day, Reason = "receiving",
                CreatedAtUtc = new DateTime(2026, 10, day, 9, 0, 0, DateTimeKind.Utc),
            });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Item_movements_carry_the_running_balance_and_the_page_s_opening_balance()
    {
        await using var h = await NewAsync();
        await SeedMovementsAsync(h.Db);
        var (_, admin) = await BranchLockTests.UserAsync(h.Db, UserRole.Admin, Algiers);
        var route = ("productId", Pipe.ToString());
        var pattern = Items + "/{productId:guid}/movements/page";

        var detail = (await h.GetAsync(ReportRoutes, Items + "/{productId:guid}", admin, "", route)).GetProperty("movements");
        var first = await h.GetAsync(ReportRoutes, pattern, admin, "", route);
        Assert.Equal(13, first.GetProperty("total").GetInt32());
        Assert.Equal(Enumerable.Range(1, 10).Select(b => (decimal)b), Balances(first.GetProperty("items")));
        var summary = first.GetProperty("summary");
        Assert.Equal(0m, summary.GetProperty("pageOpening").GetDecimal());
        Assert.Equal(0m, summary.GetProperty("opening").GetDecimal());
        Assert.Equal(13m, summary.GetProperty("closing").GetDecimal());
        Assert.Equal(detail.GetProperty("unrecordedDifference").GetRawText(), summary.GetProperty("unrecordedDifference").GetRawText());

        var second = await h.GetAsync(ReportRoutes, pattern, admin, "?skip=10&take=10", route);
        Assert.Equal([11m, 12m, 13m], Balances(second.GetProperty("items")));
        Assert.Equal(10m, second.GetProperty("summary").GetProperty("pageOpening").GetDecimal());

        // The two pages are the breakdown's rows, balance for balance.
        Assert.Equal(Balances(detail.GetProperty("rows")),
            Balances(first.GetProperty("items")).Concat(Balances(second.GetProperty("items"))));

        // From the 4th (the company's day): three receipts stand before the period, so it opens at 3.
        var period = await h.GetAsync(ReportRoutes, pattern, admin, "?from=2026-10-04", route);
        Assert.Equal(10, period.GetProperty("total").GetInt32());
        Assert.Equal(3m, period.GetProperty("summary").GetProperty("opening").GetDecimal());
        Assert.Equal(3m, period.GetProperty("summary").GetProperty("pageOpening").GetDecimal());
        var later = await h.GetAsync(ReportRoutes, pattern, admin, "?from=2026-10-04&skip=5&take=5", route);
        Assert.Equal([9m, 10m, 11m, 12m, 13m], Balances(later.GetProperty("items")));
        Assert.Equal(8m, later.GetProperty("summary").GetProperty("pageOpening").GetDecimal());
    }

    [Fact]
    public async Task Item_overview_is_the_breakdown_without_its_two_long_lists()
    {
        await using var h = await NewAsync();
        await SeedMovementsAsync(h.Db);
        var (_, admin) = await BranchLockTests.UserAsync(h.Db, UserRole.Admin, Algiers);
        var route = ("productId", Pipe.ToString());

        var detail = await h.GetAsync(ReportRoutes, Items + "/{productId:guid}", admin, "", route);
        var overview = await h.GetAsync(ReportRoutes, Items + "/{productId:guid}/overview", admin, "", route);
        Assert.Equal(detail.GetProperty("item").GetRawText(), overview.GetProperty("item").GetRawText());
        Assert.Equal(detail.GetProperty("locations").GetRawText(), overview.GetProperty("locations").GetRawText());
        Assert.Equal(0, overview.GetProperty("rented").GetArrayLength());
        Assert.Equal(0, overview.GetProperty("movements").GetProperty("rows").GetArrayLength());
        Assert.Equal(13m, overview.GetProperty("movements").GetProperty("closing").GetDecimal());
    }

    /// <summary>
    /// Pipes out on rent: three since 1 October at Algiers on a seven-day line (overdue), two since
    /// yesterday at Oran on a year's line.
    /// </summary>
    private static async Task SeedRentedPipesAsync(PosDbContext db)
    {
        var customer = new Customer { Code = "C-R1", FullName = "Entreprise Benali" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var outings = new[]
        {
            (Number: "LOC-R1", Branch: Algiers, Days: 7, Qty: 3m, At: new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc)),
            (Number: "LOC-R2", Branch: Oran, Days: 365, Qty: 2m, At: DateTime.UtcNow.AddDays(-1)),
        };
        var n = 0;
        foreach (var o in outings)
        {
            n++;
            var line = new RentalContractLine
            {
                ProductId = Pipe, Sku = "PIPE-90", NameSnapshot = "Tube 90", Qty = o.Qty + 2m, DurationDays = o.Days,
                RentUnitPrice = 10m, DamageUnitPrice = 100m, OverdueUnitPrice = 15m,
            };
            var contract = new RentalContract
            {
                Number = o.Number, CustomerId = customer.Id, BranchId = o.Branch, Status = RentalContractStatus.Open,
                LockedAtUtc = o.At, SignedAtUtc = o.At, Lines = [line],
            };
            db.RentalContracts.Add(contract);
            db.IssueNotes.Add(new IssueNote
            {
                Number = $"BS-R{n}", ContractId = contract.Id, BranchId = o.Branch, IssuedAtUtc = o.At,
                Lines = [new IssueNoteLine { ContractLineId = line.Id, Qty = o.Qty }],
            });
        }
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Item_rentals_out_page_and_filter_on_the_server_within_the_caller_s_scope()
    {
        await using var h = await NewAsync();
        await SeedRentedPipesAsync(h.Db);
        var (_, admin) = await BranchLockTests.UserAsync(h.Db, UserRole.Admin, Algiers);
        var route = ("productId", Pipe.ToString());
        var pattern = Items + "/{productId:guid}/rented/page";

        var detail = await h.GetAsync(ReportRoutes, Items + "/{productId:guid}", admin, "", route);
        var all = await h.GetAsync(ReportRoutes, pattern, admin, "", route);
        Assert.Equal(["LOC-R1", "LOC-R2"], Strings(all, "contractNumber"));
        Assert.Equal(ListStrings(detail.GetProperty("rented"), "contractNumber"), Strings(all, "contractNumber"));
        Assert.Equal(2, all.GetProperty("summary").GetProperty("count").GetInt32());
        Assert.Equal([Algiers, Oran],
            all.GetProperty("summary").GetProperty("issueBranches").EnumerateArray().Select(b => b.GetProperty("id").GetGuid()));

        Assert.Equal(["LOC-R1"], Strings(await h.GetAsync(ReportRoutes, pattern, admin, "?state=overdue", route), "contractNumber"));
        Assert.Equal(["LOC-R2"], Strings(await h.GetAsync(ReportRoutes, pattern, admin, "?state=onTime", route), "contractNumber"));
        var atOran = await h.GetAsync(ReportRoutes, pattern, admin, $"?issueBranchId={Oran}", route);
        Assert.Equal(["LOC-R2"], Strings(atOran, "contractNumber"));
        // The count stays the article's, whatever the two filters keep.
        Assert.Equal(2, atOran.GetProperty("summary").GetProperty("count").GetInt32());

        // A branch user sees their own branch's rentals and nothing of another's.
        var (_, manager) = await BranchLockTests.UserAsync(h.Db, UserRole.Manager, Oran);
        var own = await h.GetAsync(ReportRoutes, pattern, manager, $"?branchId={Algiers}", route);
        Assert.Equal(["LOC-R2"], Strings(own, "contractNumber"));
        Assert.Equal(1, own.GetProperty("summary").GetProperty("count").GetInt32());
    }

    // ---------- rental contracts ----------

    /// <summary>
    /// LOC-000001…LOC-000012 signed at 10:00 on the 1st…11th of October, the 12th at 23:30 UTC on the 12th —
    /// already the 13th in Algiers. Odd at Algiers, even at Oran. 1–4 for مؤسسة الأمل, the rest Entreprise
    /// Benali. 1 and 2 not issued yet; 3–10 issued k pipes; 4 has two back; 5 charged 1 000, 6 charged 500
    /// and 200 overdue; 11 closed (charged 9 999), 12 cancelled.
    /// </summary>
    private static async Task SeedContractsAsync(PosDbContext db)
    {
        var amal = new Customer { Code = "C-K1", FullName = "مؤسسة الأمل" };
        var benali = new Customer { Code = "C-K2", FullName = "Entreprise Benali" };
        db.Customers.AddRange(amal, benali);
        await db.SaveChangesAsync();
        for (var k = 1; k <= 12; k++)
        {
            var signed = k == 12 ? new DateTime(2026, 10, 12, 23, 30, 0, DateTimeKind.Utc) : new DateTime(2026, 10, k, 10, 0, 0, DateTimeKind.Utc);
            var branch = k % 2 == 1 ? Algiers : Oran;
            var line = new RentalContractLine
            {
                ProductId = Pipe, Sku = "PIPE-90", NameSnapshot = "Tube 90", Qty = k, DurationDays = 30,
                RentUnitPrice = 10m, DamageUnitPrice = 100m, OverdueUnitPrice = 15m,
            };
            var contract = new RentalContract
            {
                Number = $"LOC-{k:000000}", CustomerId = k <= 4 ? amal.Id : benali.Id, BranchId = branch,
                Status = k == 11 ? RentalContractStatus.Closed : k == 12 ? RentalContractStatus.Cancelled : RentalContractStatus.Open,
                LockedAtUtc = k <= 2 ? null : signed, SignedAtUtc = signed, Lines = [line],
            };
            db.RentalContracts.Add(contract);
            if (k is >= 3 and <= 10)
                db.IssueNotes.Add(new IssueNote
                {
                    Number = $"BS-{k:000000}", ContractId = contract.Id, BranchId = branch, IssuedAtUtc = signed,
                    Lines = [new IssueNoteLine { ContractLineId = line.Id, Qty = k }],
                });
            if (k == 4)
                db.ReturnNotes.Add(new ReturnNote
                {
                    Number = "BR-000004", ContractId = contract.Id, BranchId = branch, ReturnedAtUtc = signed.AddDays(2),
                    Lines = [new ReturnNoteLine { ContractLineId = line.Id, QtyOk = 2m }],
                });
            void Charge(RentalChargeKind kind, decimal amount) => db.RentalCharges.Add(new RentalCharge
            {
                ContractId = contract.Id, ContractLineId = line.Id, Kind = kind, Amount = amount, ReturnNoteId = Guid.NewGuid(),
            });
            if (k == 5) Charge(RentalChargeKind.Rent, 1000m);
            if (k == 6) { Charge(RentalChargeKind.Rent, 500m); Charge(RentalChargeKind.Overdue, 200m); }
            if (k == 11) Charge(RentalChargeKind.Rent, 9999m);
        }
        await db.SaveChangesAsync();
    }

    private const string Contracts = "/api/rentals/contracts/page";

    private static void AssertTiles(JsonElement page)
    {
        var tiles = page.GetProperty("summary").GetProperty("totals");
        // Open contracts only: 3+…+10 issued, two of them back; the closed one's 9 999 is not counted.
        Assert.Equal(50m, tiles.GetProperty("stillOut").GetDecimal());
        Assert.Equal(1700m, tiles.GetProperty("charged").GetDecimal());
        Assert.Equal(200m, tiles.GetProperty("overdue").GetDecimal());
        Assert.Equal(2, tiles.GetProperty("drafts").GetInt32());
        Assert.Equal(8, tiles.GetProperty("openWithGoods").GetInt32());
    }

    [Fact]
    public async Task Contracts_ten_newest_signed_first_with_the_yard_s_tiles_over_every_contract()
    {
        await using var h = await NewAsync();
        await SeedContractsAsync(h.Db);
        var (_, admin) = await BranchLockTests.UserAsync(h.Db, UserRole.Admin, Algiers);

        var first = await h.GetAsync(RentalRoutes, Contracts, admin);
        Assert.Equal(12, first.GetProperty("total").GetInt32());
        Assert.Equal(Enumerable.Range(3, 10).Reverse().Select(k => $"LOC-{k:000000}"), Strings(first, "number"));
        AssertTiles(first);
        Assert.Equal(12, first.GetProperty("summary").GetProperty("count").GetInt32());
        Assert.False(first.GetProperty("summary").GetProperty("depositVisible").GetBoolean());

        var second = await h.GetAsync(RentalRoutes, Contracts, admin, "?skip=10");
        Assert.Equal(["LOC-000002", "LOC-000001"], Strings(second, "number"));
        var past = await h.GetAsync(RentalRoutes, Contracts, admin, "?skip=90&take=20");
        Assert.Equal(0, past.GetProperty("skip").GetInt32());
        Assert.Equal(12, past.GetProperty("items").GetArrayLength());

        var open = await h.GetAsync(RentalRoutes, Contracts, admin, "?status=Open");
        Assert.Equal(10, open.GetProperty("total").GetInt32());
        Assert.Equal(["draft", "out", "partly"], open.GetProperty("summary").GetProperty("stages").EnumerateArray().Select(s => s.GetString()));
        AssertTiles(open);

        // The row's figures are the list's: what is out, what was charged.
        var six = open.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("number").GetString() == "LOC-000006");
        Assert.Equal(6m, six.GetProperty("stillOutQty").GetDecimal());
        Assert.Equal(700m, six.GetProperty("chargeTotal").GetDecimal());
        Assert.Equal(200m, six.GetProperty("overdueTotal").GetDecimal());
    }

    [Theory]
    // The till's fold: موسسه الامل finds مؤسسة الأمل; the number is searched too.
    [InlineData("?q=" + "%D9%85%D9%88%D8%B3%D8%B3%D9%87", new[] { "LOC-000004", "LOC-000003", "LOC-000002", "LOC-000001" })]
    [InlineData("?q=loc-000007", new[] { "LOC-000007" })]
    [InlineData("?stage=partly", new[] { "LOC-000004" })]
    [InlineData("?stage=draft", new[] { "LOC-000002", "LOC-000001" })]
    [InlineData("?stage=closed", new[] { "LOC-000011" })]
    [InlineData("?late=yes", new[] { "LOC-000006" })]
    [InlineData("?branch=22222222-2222-2222-2222-222222222222", new[] { "LOC-000012", "LOC-000010", "LOC-000008", "LOC-000006", "LOC-000004", "LOC-000002" })]
    // Signed on the company's calendar: LOC-000012 was signed on the 12th in UTC, the 13th in Algiers.
    [InlineData("?from=2026-10-13", new[] { "LOC-000012" })]
    [InlineData("?from=2026-10-11&to=2026-10-12", new[] { "LOC-000011" })]
    public async Task Contracts_filter_on_the_server_and_the_tiles_stay_the_yard_s(string query, string[] expected)
    {
        await using var h = await NewAsync();
        await SeedContractsAsync(h.Db);
        var (_, admin) = await BranchLockTests.UserAsync(h.Db, UserRole.Admin, Algiers);

        var page = await h.GetAsync(RentalRoutes, Contracts, admin, query);
        Assert.Equal(expected, Strings(page, "number"));
        Assert.Equal(expected.Length, page.GetProperty("total").GetInt32());
        // The tiles are the state chip's and the customer's, as the screen summed them: the search does not move them.
        AssertTiles(page);
    }

    [Fact]
    public async Task A_branch_user_pages_their_own_branch_s_contracts_and_no_cancelled_one()
    {
        await using var h = await NewAsync();
        await SeedContractsAsync(h.Db);
        var (_, manager) = await BranchLockTests.UserAsync(h.Db, UserRole.Manager, Oran);

        // Oran's: 2, 4, 6, 8, 10 — and 12, cancelled, which only rentals.cancelled reads.
        var page = await h.GetAsync(RentalRoutes, Contracts, manager, $"?branchId={Algiers}");
        Assert.Equal(["LOC-000010", "LOC-000008", "LOC-000006", "LOC-000004", "LOC-000002"], Strings(page, "number"));
        // Its tiles are its branch's: 10+8+6+4 issued, two back; 700 charged on LOC-000006.
        var tiles = page.GetProperty("summary").GetProperty("totals");
        Assert.Equal(26m, tiles.GetProperty("stillOut").GetDecimal());
        Assert.Equal(700m, tiles.GetProperty("charged").GetDecimal());
        Assert.Equal(1, tiles.GetProperty("drafts").GetInt32());
    }

    // ---------- items on a rental figure of their own ----------

    /// <summary>
    /// RNT-01…RNT-12: a hire price of 100×i except every third (an overdue multiplier instead), a replacement
    /// value on the even ones; سقالة (SCF-1) at 300; and an item on no figure of its own, which is not listed.
    /// </summary>
    private static async Task SeedItemFiguresAsync(PosDbContext db)
    {
        for (var i = 1; i <= 12; i++)
            db.Products.Add(new Product
            {
                Sku = $"RNT-{i:00}", NameFr = $"Panneau {i}", Price = 10m, IsRentable = true,
                RentalUnitPrice = i % 3 == 0 ? 0m : 100m * i,
                RentalOverdueMultiplier = i % 3 == 0 ? 2m : null,
                RentalDamageUnitPrice = i % 2 == 0 ? 5000m : null,
            });
        db.Products.Add(new Product { Sku = "SCF-1", NameFr = "Échafaudage", NameAr = "سقالة", Price = 10m, IsRentable = true, RentalUnitPrice = 300m });
        db.Products.Add(new Product { Sku = "PLAIN-1", NameFr = "Sans figure", Price = 10m });
        await db.SaveChangesAsync();
    }

    private const string ItemFigures = "/api/rentals/settings/products/page";

    [Fact]
    public async Task Item_figures_ten_first_by_sku_as_the_list_reads_them()
    {
        await using var h = await NewAsync();
        await SeedItemFiguresAsync(h.Db);
        var (_, admin) = await BranchLockTests.UserAsync(h.Db, UserRole.Admin, Algiers);

        var first = await h.GetAsync(RentalRoutes, ItemFigures, admin);
        Assert.Equal(13, first.GetProperty("total").GetInt32());
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"RNT-{i:00}"), Strings(first, "sku"));
        Assert.Equal(["RNT-11", "RNT-12", "SCF-1"], Strings(await h.GetAsync(RentalRoutes, ItemFigures, admin, "?skip=10"), "sku"));

        // The rows the list route reads (RentalDocuments.ProductMultipliersAsync), in its order.
        var list = await new RentalDocuments(h.Db, new LedgerPoster(h.Db), new Algeria()).ProductMultipliersAsync();
        var all = await h.GetAsync(RentalRoutes, ItemFigures, admin, "?take=100");
        Assert.Equal(list.Select(r => r.Sku), Strings(all, "sku"));
    }

    [Theory]
    [InlineData("?price=set", 9)]
    [InlineData("?price=unset", 4)]
    [InlineData("?damage=set", 6)]
    [InlineData("?damage=unset", 7)]
    [InlineData("?price=set&damage=set", 4)]
    [InlineData("?q=rnt-1", 3)]
    // The till's fold: سقاله finds سقالة.
    [InlineData("?q=" + "%D8%B3%D9%82%D8%A7%D9%84%D9%87", 1)]
    public async Task Item_figures_filter_and_search_on_the_server(string query, int expected)
    {
        await using var h = await NewAsync();
        await SeedItemFiguresAsync(h.Db);
        var (_, admin) = await BranchLockTests.UserAsync(h.Db, UserRole.Admin, Algiers);

        Assert.Equal(expected, (await h.GetAsync(RentalRoutes, ItemFigures, admin, query)).GetProperty("total").GetInt32());
    }
}
