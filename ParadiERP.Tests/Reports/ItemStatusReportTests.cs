using System.Security.Claims;
using System.Text.Json;
using ParadiERP.API.Endpoints;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Catalog;
using ParadiERP.Infrastructure.Ledger;
using ParadiERP.Infrastructure.Persistence;
using ParadiERP.Infrastructure.Rentals;
using ParadiERP.Infrastructure.Reports;
using ParadiERP.Tests.Rentals;
using ParadiERP.Tests.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ParadiERP.Tests.Reports;

/// <summary>
/// Verifies the server read side of Item Movement &amp; Status report across reconciliation,
/// administrative scoping, branch-locked isolation, stock viewing grants, rental engine
/// FIFO synchronization, export consistency, and chronological movement ledgering.
/// </summary>
public class ItemStatusReportTests
{
    private sealed class Algeria : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => new()
        {
            CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers"
        };
    }

    [Fact]
    public async Task Total_is_the_sum_of_the_states_on_every_row_and_in_the_totals_an_inactive_warehouse_included()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;

        var warehouseId = Guid.NewGuid();
        db.Branches.Add(new Branch
        {
            Id = warehouseId,
            Code = "WH-01",
            NameAr = "مستودع وهران",
            NameFr = "Dépôt Oran",
            NameEn = "Oran Warehouse",
            IsWarehouse = true,
            IsActive = false, // proves inactive locations count
            IsOpenForOperation = true
        });

        var sAlgiers = await db.ProductStocks.SingleAsync(s => s.ProductId == TillHarness.Pipe && s.BranchId == TillHarness.Algiers);
        sAlgiers.StockQty = 10m;
        sAlgiers.OnRentQty = 4m;
        sAlgiers.MaintenanceQty = 2m;
        sAlgiers.ScrapQty = 1m;

        db.ProductStocks.Add(new ProductStock
        {
            ProductId = TillHarness.Pipe,
            BranchId = warehouseId,
            StockQty = 25m,
            OnRentQty = 0m,
            MaintenanceQty = 5m,
            ScrapQty = 0m
        });
        await db.SaveChangesAsync();

        var (_, admin) = await BranchLockTests.UserAsync(db, UserRole.Admin, TillHarness.Algiers);
        var (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapReportEndpoints(), "GET",
            "/api/reports/inventory/items", admin, "");
        Assert.Equal(200, status);

        var doc = JsonDocument.Parse(body);
        var rows = doc.RootElement.GetProperty("rows").EnumerateArray().ToList();
        Assert.NotEmpty(rows);

        foreach (var row in rows)
        {
            var total = row.GetProperty("total").GetDecimal();
            var wh = row.GetProperty("warehouses").GetDecimal();
            var br = row.GetProperty("branches").GetDecimal();
            var rnt = row.GetProperty("rented").GetDecimal();
            var mnt = row.GetProperty("maintenance").GetDecimal();
            var scr = row.GetProperty("scrap").GetDecimal();
            var av = row.GetProperty("available").GetDecimal();

            Assert.Equal(wh + br + rnt + mnt + scr, total);
            Assert.Equal(wh + br, av);
        }

        var totals = doc.RootElement.GetProperty("totals");
        var tTotal = totals.GetProperty("total").GetDecimal();
        var tWh = totals.GetProperty("warehouses").GetDecimal();
        var tBr = totals.GetProperty("branches").GetDecimal();
        var tRnt = totals.GetProperty("rented").GetDecimal();
        var tMnt = totals.GetProperty("maintenance").GetDecimal();
        var tScr = totals.GetProperty("scrap").GetDecimal();
        var tAv = totals.GetProperty("available").GetDecimal();

        Assert.Equal(tWh + tBr + tRnt + tMnt + tScr, tTotal);
        Assert.Equal(tWh + tBr, tAv);
    }

    [Fact]
    public async Task The_admin_reads_every_location_or_the_one_he_names()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (_, admin) = await BranchLockTests.UserAsync(db, UserRole.Admin, TillHarness.Algiers);

        // 1. Admin with no branchId sees every location
        var (status1, body1) = await BranchLockTests.InvokeAsync(db, app => app.MapReportEndpoints(), "GET",
            "/api/reports/inventory/items", admin, "");
        Assert.Equal(200, status1);
        var doc1 = JsonDocument.Parse(body1);
        Assert.Equal("company", doc1.RootElement.GetProperty("scope").GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, doc1.RootElement.GetProperty("scope").GetProperty("locationId").ValueKind);
        Assert.Equal(20m, doc1.RootElement.GetProperty("totals").GetProperty("available").GetDecimal());

        // 2. Admin with branchId = Oran sees Oran only (scope "location")
        var (status2, body2) = await BranchLockTests.InvokeAsync(db, app => app.MapReportEndpoints(), "GET",
            "/api/reports/inventory/items", admin, $"?branchId={TillHarness.Oran}");
        Assert.Equal(200, status2);
        var doc2 = JsonDocument.Parse(body2);
        Assert.Equal("location", doc2.RootElement.GetProperty("scope").GetProperty("kind").GetString());
        Assert.Equal(TillHarness.Oran, doc2.RootElement.GetProperty("scope").GetProperty("locationId").GetGuid());
        Assert.Equal(10m, doc2.RootElement.GetProperty("totals").GetProperty("available").GetDecimal());
    }

    [Fact]
    public async Task Without_the_grant_a_branch_manager_reads_their_branch_alone_whatever_branch_is_asked()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (_, manager) = await BranchLockTests.UserAsync(db, UserRole.Manager, TillHarness.Oran);

        // Summary: asking ?branchId=<Algiers> still scopes to Oran
        var (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapReportEndpoints(), "GET",
            "/api/reports/inventory/items", manager, $"?branchId={TillHarness.Algiers}");
        Assert.Equal(200, status);
        var doc = JsonDocument.Parse(body);
        Assert.Equal("own", doc.RootElement.GetProperty("scope").GetProperty("kind").GetString());
        Assert.Equal(TillHarness.Oran, doc.RootElement.GetProperty("scope").GetProperty("locationId").GetGuid());
        Assert.False(doc.RootElement.GetProperty("scope").GetProperty("othersAvailableShown").GetBoolean());

        var row = doc.RootElement.GetProperty("rows")[0];
        Assert.Equal(10m, row.GetProperty("available").GetDecimal());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("otherAvailable").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("totals").GetProperty("otherAvailable").ValueKind);

        // Detail: Locations contains only Oran
        var (dStatus, dBody) = await BranchLockTests.InvokeAsync(db, app => app.MapReportEndpoints(), "GET",
            "/api/reports/inventory/items/{productId:guid}", manager, $"?branchId={TillHarness.Algiers}",
            ("productId", TillHarness.Pipe.ToString()));
        Assert.Equal(200, dStatus);
        var dDoc = JsonDocument.Parse(dBody);
        var locs = dDoc.RootElement.GetProperty("locations").EnumerateArray().ToList();
        Assert.Single(locs);
        Assert.Equal(TillHarness.Oran, locs[0].GetProperty("branchId").GetGuid());
    }

    [Fact]
    public async Task With_the_grant_another_branch_shows_its_available_quantity_and_nothing_else()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (_, manager) = await BranchLockTests.UserAsync(db, UserRole.Manager, TillHarness.Oran);
        var grantedCaller = TillHarness.WithGrant(manager, Permissions.ViewOtherBranchStock);

        // Summary: OtherAvailable = Algiers' StockQty (10)
        var (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapReportEndpoints(), "GET",
            "/api/reports/inventory/items", grantedCaller, "");
        Assert.Equal(200, status);
        var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("scope").GetProperty("othersAvailableShown").GetBoolean());
        var row = doc.RootElement.GetProperty("rows")[0];
        Assert.Equal(10m, row.GetProperty("otherAvailable").GetDecimal());
        Assert.Equal(10m, doc.RootElement.GetProperty("totals").GetProperty("otherAvailable").GetDecimal());

        // Detail: Algiers has AvailableOnly=true, onRent/maintenance/scrap/total null, no Algiers contracts in Rented
        var (dStatus, dBody) = await BranchLockTests.InvokeAsync(db, app => app.MapReportEndpoints(), "GET",
            "/api/reports/inventory/items/{productId:guid}", grantedCaller, "",
            ("productId", TillHarness.Pipe.ToString()));
        Assert.Equal(200, dStatus);
        var dDoc = JsonDocument.Parse(dBody);
        var locs = dDoc.RootElement.GetProperty("locations").EnumerateArray().ToList();
        Assert.Equal(2, locs.Count);

        var algiersLoc = locs.Single(l => l.GetProperty("branchId").GetGuid() == TillHarness.Algiers);
        Assert.True(algiersLoc.GetProperty("availableOnly").GetBoolean());
        Assert.Equal(10m, algiersLoc.GetProperty("available").GetDecimal());
        Assert.Equal(JsonValueKind.Null, algiersLoc.GetProperty("onRent").ValueKind);
        Assert.Equal(JsonValueKind.Null, algiersLoc.GetProperty("maintenance").ValueKind);
        Assert.Equal(JsonValueKind.Null, algiersLoc.GetProperty("scrap").ValueKind);
        Assert.Equal(JsonValueKind.Null, algiersLoc.GetProperty("total").ValueKind);

        var rented = dDoc.RootElement.GetProperty("rented").EnumerateArray().ToList();
        Assert.DoesNotContain(rented, r => r.GetProperty("issueBranchId").GetGuid() == TillHarness.Algiers);
    }

    [Fact]
    public async Task On_rent_without_a_contract_shows_as_unexplained_and_each_batch_carries_its_expected_return()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;

        var pipe = await db.Products.SingleAsync(p => p.Id == TillHarness.Pipe);
        pipe.IsRentable = true;

        var customer = RentalTestPaper.Identified(new Customer
        {
            Code = "C001",
            FullName = "Client Test",
            Kind = CustomerKind.Person
        });
        db.Customers.Add(customer);
        var site = RentalTestPaper.Site(Guid.NewGuid(), customer.Id);
        db.CustomerSites.Add(site);
        db.Stores.Add(new Store { NameFr = "Store", RentalOverdueMultiplier = 1.5m });
        await db.SaveChangesAsync();

        var rentals = new RentalDocuments(db, new LedgerPoster(db), new Algeria());
        var contractReq = new UpsertRentalContractRequest(customer.Id, TillHarness.Algiers,
            [new RentalContractLineRequest(pipe.Id, 10m, 15, RentUnitPrice: 100m, DamageUnitPrice: 500m)])
        {
            SiteId = site.Id,
            SignatoryBirthDate = new DateOnly(1980, 3, 4)
        };
        var contractResult = await rentals.CreateContractAsync(contractReq, "admin");
        Assert.True(contractResult.Ok, contractResult.Message);
        var contract = contractResult.Value!;
        var line = contract.Lines.Single();

        var issueReq = RentalTestPaper.Driven(new IssueNoteRequest(
            TillHarness.Algiers,
            [new IssueNoteLineRequest(line.Id, 4m)],
            DateTime.UtcNow));
        var issueResult = await rentals.IssueAsync(contract.Id, issueReq, null, "admin");
        Assert.True(issueResult.Ok, issueResult.Message);

        var report = new ItemStatusReport(db, rentals, new Algeria());
        var viewer = new ItemStatusViewer(null, false);
        var filter = new ItemStatusFilter(null, null, [pipe.Id], null, null, null, null, false);

        // Before hand edit: UnexplainedOnRent == 0 and StillOut == 4
        var summaryBefore = await report.SummaryAsync(viewer, filter, default);
        var rowBefore = summaryBefore.Rows.Single(r => r.ProductId == pipe.Id);
        Assert.Equal(4m, rowBefore.Rented);
        Assert.Equal(4m, rowBefore.StillOut);
        Assert.Equal(0m, rowBefore.UnexplainedOnRent);

        // Hand edit: add 2 to OnRentQty
        var stock = await db.ProductStocks.SingleAsync(s => s.ProductId == pipe.Id && s.BranchId == TillHarness.Algiers);
        stock.OnRentQty += 2m;
        await db.SaveChangesAsync();

        // After hand edit: UnexplainedOnRent == 2 and StillOut == 4
        var summaryAfter = await report.SummaryAsync(viewer, filter, default);
        var rowAfter = summaryAfter.Rows.Single(r => r.ProductId == pipe.Id);
        Assert.Equal(6m, rowAfter.Rented);
        Assert.Equal(4m, rowAfter.StillOut);
        Assert.Equal(2m, rowAfter.UnexplainedOnRent);

        // Detail Rented: ExpectedReturnOn == IssuedOn + line DurationDays (15 days)
        var detail = (await report.DetailAsync(pipe.Id, viewer, filter, null, null, default))!;
        Assert.Equal(2m, detail.Item.UnexplainedOnRent);
        Assert.Equal(4m, detail.Item.StillOut);
        Assert.Single(detail.Rented);
        var batch = detail.Rented[0];
        Assert.Equal(4m, batch.Qty);
        Assert.Equal(batch.IssuedOn.AddDays(15), batch.ExpectedReturnOn);
    }

    [Fact]
    public async Task The_excel_totals_are_the_screen_totals()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;
        var (_, admin) = await BranchLockTests.UserAsync(db, UserRole.Admin, TillHarness.Algiers);

        var (status, body) = await BranchLockTests.InvokeAsync(db, app => app.MapReportEndpoints(), "GET",
            "/api/reports/inventory/items", admin, "");
        Assert.Equal(200, status);

        var doc = JsonDocument.Parse(body);
        var jsonTotals = doc.RootElement.GetProperty("totals");
        var totalExpected = jsonTotals.GetProperty("total").GetDecimal();
        var rentedExpected = jsonTotals.GetProperty("rented").GetDecimal();
        var availableExpected = jsonTotals.GetProperty("available").GetDecimal();

        var report = new ItemStatusReport(db, new RentalDocuments(db, new LedgerPoster(db), new Algeria()), new Algeria());
        var summary = await report.SummaryAsync(new ItemStatusViewer(null, false),
            new ItemStatusFilter(null, null, null, null, null, null, null, false), default);
        var bytes = report.SummaryWorkbook(summary, ItemStatusLabels.For("fr"));

        var sheet = XlsxReader.Read(new MemoryStream(bytes));
        var totalsRow = sheet.Rows.Last();

        Assert.Equal(totalExpected, totalsRow[5]!.Number);
        Assert.Equal(rentedExpected, totalsRow[8]!.Number);
        Assert.Equal(availableExpected, totalsRow[11]!.Number);
    }

    [Fact]
    public async Task Movements_carry_user_and_number_and_a_transfer_inside_the_scope_is_one_row()
    {
        var (connection, db) = await TillHarness.NewDbAsync();
        await using var _ = connection;
        await using var __ = db;

        var transferDocId = Guid.NewGuid();
        db.StockMovements.Add(new StockMovement
        {
            ProductId = TillHarness.Pipe,
            BranchId = TillHarness.Algiers,
            QtyDelta = 5m,
            BalanceAfter = 15m,
            Reason = "receiving: BL-1",
            DocumentNumber = "BL-1",
            UserName = "Amina",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-30)
        });
        db.StockMovements.Add(new StockMovement
        {
            ProductId = TillHarness.Pipe,
            BranchId = TillHarness.Algiers,
            QtyDelta = -2m,
            BalanceAfter = 13m,
            Reason = "sale",
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-20)
        });
        db.StockMovements.Add(new StockMovement
        {
            ProductId = TillHarness.Pipe,
            BranchId = TillHarness.Algiers,
            RelatedBranchId = TillHarness.Oran,
            QtyDelta = -3m,
            BalanceAfter = 10m,
            Reason = "transfer: inter-branch",
            DocumentId = transferDocId,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
        });
        db.StockMovements.Add(new StockMovement
        {
            ProductId = TillHarness.Pipe,
            BranchId = TillHarness.Oran,
            RelatedBranchId = TillHarness.Algiers,
            QtyDelta = 3m,
            BalanceAfter = 13m,
            Reason = "transfer: inter-branch",
            DocumentId = transferDocId,
            CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10)
        });
        await db.SaveChangesAsync();

        // 1. Admin with no branch: merged transfer pair (In=Out=3), balances running, Amina & BL-1 present, nulls on sale
        var (_, admin) = await BranchLockTests.UserAsync(db, UserRole.Admin, TillHarness.Algiers);
        var (statusAdmin, bodyAdmin) = await BranchLockTests.InvokeAsync(db, app => app.MapReportEndpoints(), "GET",
            "/api/reports/inventory/items/{productId:guid}", admin, "",
            ("productId", TillHarness.Pipe.ToString()));
        Assert.Equal(200, statusAdmin);

        var adminDoc = JsonDocument.Parse(bodyAdmin);
        var adminMovements = adminDoc.RootElement.GetProperty("movements").GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(3, adminMovements.Count);

        var r0 = adminMovements[0];
        Assert.Equal("receiving", r0.GetProperty("type").GetString());
        Assert.Equal("BL-1", r0.GetProperty("documentNumber").GetString());
        Assert.Equal("Amina", r0.GetProperty("userName").GetString());
        Assert.Equal(5m, r0.GetProperty("in").GetDecimal());
        Assert.Equal(0m, r0.GetProperty("out").GetDecimal());
        Assert.Equal(5m, r0.GetProperty("balance").GetDecimal());

        var r1 = adminMovements[1];
        Assert.Equal("sale", r1.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, r1.GetProperty("documentNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, r1.GetProperty("userName").ValueKind);
        Assert.Equal(0m, r1.GetProperty("in").GetDecimal());
        Assert.Equal(2m, r1.GetProperty("out").GetDecimal());
        Assert.Equal(3m, r1.GetProperty("balance").GetDecimal());

        var r2 = adminMovements[2];
        Assert.Equal("transfer", r2.GetProperty("type").GetString());
        Assert.Equal(3m, r2.GetProperty("in").GetDecimal());
        Assert.Equal(3m, r2.GetProperty("out").GetDecimal());
        Assert.Equal(3m, r2.GetProperty("moved").GetDecimal());
        Assert.Equal(3m, r2.GetProperty("balance").GetDecimal());
        Assert.Equal(TillHarness.Algiers, r2.GetProperty("fromBranchId").GetGuid());
        Assert.Equal(TillHarness.Oran, r2.GetProperty("toBranchId").GetGuid());

        // 2. Oran Manager: transfer is one row with In=3
        var (_, manager) = await BranchLockTests.UserAsync(db, UserRole.Manager, TillHarness.Oran);
        var (statusMgr, bodyMgr) = await BranchLockTests.InvokeAsync(db, app => app.MapReportEndpoints(), "GET",
            "/api/reports/inventory/items/{productId:guid}", manager, "",
            ("productId", TillHarness.Pipe.ToString()));
        Assert.Equal(200, statusMgr);

        var mgrDoc = JsonDocument.Parse(bodyMgr);
        var mgrMovements = mgrDoc.RootElement.GetProperty("movements").GetProperty("rows").EnumerateArray().ToList();
        Assert.Single(mgrMovements);

        var m0 = mgrMovements[0];
        Assert.Equal("transfer", m0.GetProperty("type").GetString());
        Assert.Equal(3m, m0.GetProperty("in").GetDecimal());
        Assert.Equal(0m, m0.GetProperty("out").GetDecimal());
        Assert.Equal(3m, m0.GetProperty("balance").GetDecimal());
        Assert.Equal(TillHarness.Algiers, m0.GetProperty("fromBranchId").GetGuid());
        Assert.Equal(TillHarness.Oran, m0.GetProperty("toBranchId").GetGuid());
    }
}
