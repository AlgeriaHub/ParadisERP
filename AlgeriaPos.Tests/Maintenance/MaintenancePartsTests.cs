using System.Text.Json;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Maintenance;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Valuation;
using AlgeriaPos.Tests.Manufacturing;
using Microsoft.EntityFrameworkCore;
using static AlgeriaPos.Tests.Maintenance.MaintenanceWorld;

namespace AlgeriaPos.Tests.Maintenance;

/// <summary>
/// Parts and outside work on a job, Phase M2 (docs/maintenance/plan.md §2.3, §2.4, §8 row M2): parts issued from the job's
/// branch stock and returned to it, one movement per part, at the cost method in force and back at the cost they left at;
/// never beyond what the branch holds or what production there still claims; retried safely; nothing posted (MS-32); the
/// job closed with parts still at it; outside work under one voucher cap with production orders; branch scope and MS-01.
/// The parts: welding rods carried at 40 (fifty at Algiers, ten at Oran) and a labour item that keeps no stock.
/// </summary>
public class MaintenancePartsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static string Json(object o) => JsonSerializer.Serialize(o, Web);
    private static string? CodeOf(string body) =>
        JsonDocument.Parse(body).RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;

    private static readonly Guid Rod = Guid.Parse("a2a2a2a2-0000-0000-0000-000000000001");
    private static readonly Guid Paint = Guid.Parse("a2a2a2a2-0000-0000-0000-000000000002");
    private static readonly Guid Service = Guid.Parse("a2a2a2a2-0000-0000-0000-000000000003");

    private sealed class Algeria : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => new() { CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers" };
    }

    private static MaintenanceJobParts Parts(PosDbContext db) => new(db, new Algeria());

    /// <summary>A maintenance world (the module as <paramref name="settings"/> says, ten scaffolds in maintenance) with the parts on the shelf.</summary>
    private static async Task<MaintenanceWorld> WorldAsync(Action<MaintenanceSettings>? settings = null)
    {
        var w = await NewAsync(settings ?? On(), inMaintenance: 10);
        w.Db.Products.AddRange(
            new Product { Id = Rod, Sku = "ROD-25", NameFr = "Baguette 2,5", NameAr = "قضيب", NameEn = "Rod", CostPrice = 40m, TrackStock = true,
                ManufacturingRole = ManufacturingRole.SparePart },
            new Product { Id = Paint, Sku = "PNT-01", NameFr = "Peinture", NameAr = "طلاء", NameEn = "Paint", CostPrice = 900m, TrackStock = true,
                ManufacturingRole = ManufacturingRole.Consumable },
            new Product { Id = Service, Sku = "SRV-01", NameFr = "Main d'œuvre", NameAr = "يد عاملة", NameEn = "Labour", CostPrice = 0m, TrackStock = false });
        w.Db.ProductStocks.AddRange(
            new ProductStock { ProductId = Rod, BranchId = Algiers, StockQty = 50m },
            new ProductStock { ProductId = Rod, BranchId = Oran, StockQty = 10m },
            new ProductStock { ProductId = Paint, BranchId = Algiers, StockQty = 4m });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        return w;
    }

    private static MaintenancePartsRequest Move(string kind, params (Guid Product, decimal Qty)[] lines) =>
        new(Guid.NewGuid().ToString("N"), kind, lines.Select(l => new MaintenancePartLineRequest(l.Product, l.Qty)).ToList(), null);

    private static async Task<MaintenanceJobWorkDto> OkAsync(Task<MaintenanceResult<MaintenanceJobWorkDto>> call)
    {
        var result = await call;
        Assert.True(result.Ok, result.Error);
        return result.Value!;
    }

    // ---------- stock and movements ----------

    [Fact]
    public async Task Issuing_and_returning_move_the_branch_stock_one_movement_per_part_back_at_the_cost_it_left_and_post_nothing()
    {
        await using var w = await WorldAsync();
        var job = await w.OpenAsync(Internal(2));
        var parts = Parts(w.Db);
        var entries = await w.Db.LedgerEntries.CountAsync();

        var issued = await OkAsync(parts.MoveAsync(job.Id, Move("Issue", (Rod, 20), (Paint, 1), (Rod, 2)), Chef));
        Assert.Equal((28m, 4m - 1m), ((await w.StockAsync(Algiers, Rod)).StockQty, (await w.StockAsync(Algiers, Paint)).StockQty));
        Assert.Equal(10m, (await w.StockAsync(Oran, Rod)).StockQty);
        var outRows = await w.Db.StockMovements.AsNoTracking().Where(m => m.Kind == StockMovementKind.MaintenanceIssue).OrderBy(m => m.ProductId).ToListAsync();
        Assert.Equal(2, outRows.Count);
        var rod = outRows.Single(m => m.ProductId == Rod);
        // The same product twice in a request is one movement of the sum, at the cost price while sales are costed at it.
        Assert.Equal((Algiers, -22m, 22m, 28m, 40m, SaleCostBasis.CostPrice, "maint-issue", "MJI-000001", Chef.UserId),
            (rod.BranchId, rod.QtyDelta, rod.MovedQty!.Value, rod.BalanceAfter, rod.UnitCost!.Value, rod.CostBasis, rod.Reason, rod.DocumentNumber!, rod.UserId));
        Assert.Single(outRows.Select(m => m.DocumentId).Distinct());
        Assert.Equal(22m * 40m + 900m, issued.PartsValue);
        Assert.Equal(23m, issued.PartsAtJobQty);
        Assert.Equal("PracticeLedgerByMonth", issued.PartsPosting);

        // The cost price changes; the rods come back at what they left at, not at the new price.
        await w.Db.Products.Where(p => p.Id == Rod).ExecuteUpdateAsync(s => s.SetProperty(p => p.CostPrice, 55m));
        var back = await OkAsync(parts.MoveAsync(job.Id, Move("Return", (Rod, 5)), Chef));
        var inRow = await w.Db.StockMovements.AsNoTracking().SingleAsync(m => m.Kind == StockMovementKind.MaintenanceReturn);
        Assert.Equal((5m, 33m, 40m, SaleCostBasis.CostPrice, "maint-return", "MJR-000001"),
            (inRow.QtyDelta, inRow.BalanceAfter, inRow.UnitCost!.Value, inRow.CostBasis, inRow.Reason, inRow.DocumentNumber!));
        Assert.Equal(33m, (await w.StockAsync(Algiers, Rod)).StockQty);
        var rodLine = back.Parts.Single(p => p.ProductId == Rod);
        Assert.Equal((22m, 5m, 17m, 40m, 680m), (rodLine.IssuedQty, rodLine.ReturnedQty, rodLine.AtJobQty, rodLine.IssueUnitCost, rodLine.Value));
        Assert.Equal(680m + 900m, back.PartsValue);
        Assert.Equal(["Issue", "Issue", "Return"], back.Movements.Select(m => m.Kind).Order(StringComparer.Ordinal));

        // MS-32 by default: parts reach the books through the practice ledger (M6), so nothing posts at issue or return.
        Assert.Equal(entries, await w.Db.LedgerEntries.CountAsync());
        var events = (await w.Jobs.GetAsync(job.Id)).Value!.Events.Select(e => e.Kind).ToList();
        Assert.Equal(["Opened", "PartsIssued", "PartsReturned"], events);
    }

    [Fact]
    public async Task An_issue_is_refused_beyond_the_branch_stock_a_return_beyond_what_is_at_the_job_and_nothing_moves()
    {
        await using var w = await WorldAsync();
        var job = await w.OpenAsync(Internal(2));
        var parts = Parts(w.Db);

        var tooMany = await parts.MoveAsync(job.Id, Move("Issue", (Rod, 10), (Paint, 5)), Chef);
        Assert.Equal((409, "insufficient_stock"), (tooMany.Status, tooMany.Code));
        Assert.Equal((400, "not_stocked"), ((await parts.MoveAsync(job.Id, Move("Issue", (Service, 1)), Chef)) is var s ? (s.Status, s.Code) : default));
        Assert.Equal(404, (await parts.MoveAsync(job.Id, Move("Issue", (Guid.NewGuid(), 1)), Chef)).Status);
        Assert.Equal((400, "invalid_quantity"), ((await parts.MoveAsync(job.Id, Move("Issue", (Rod, 0)), Chef)) is var z ? (z.Status, z.Code) : default));
        Assert.Equal((400, "invalid_kind"), ((await parts.MoveAsync(job.Id, Move("Scrap", (Rod, 1)), Chef)) is var k ? (k.Status, k.Code) : default));
        Assert.Equal((409, "over_balance"), ((await parts.MoveAsync(job.Id, Move("Return", (Rod, 1)), Chef)) is var r ? (r.Status, r.Code) : default));

        await OkAsync(parts.MoveAsync(job.Id, Move("Issue", (Rod, 50)), Chef));
        Assert.Equal((409, "insufficient_stock"), ((await parts.MoveAsync(job.Id, Move("Issue", (Rod, 1)), Chef)) is var e ? (e.Status, e.Code) : default));
        Assert.Equal((409, "over_balance"), ((await parts.MoveAsync(job.Id, Move("Return", (Rod, 50.5m)), Chef)) is var o ? (o.Status, o.Code) : default));
        Assert.Equal((0m, 4m), ((await w.StockAsync(Algiers, Rod)).StockQty, (await w.StockAsync(Algiers, Paint)).StockQty));
        Assert.Equal(1, await w.Db.StockMovements.CountAsync(m => m.Kind == StockMovementKind.MaintenanceIssue));
        Assert.Equal(0, await w.Db.StockMovements.CountAsync(m => m.Kind == StockMovementKind.MaintenanceReturn));
    }

    [Fact]
    public async Task A_retried_request_moves_nothing_again_and_another_job_s_request_id_is_refused()
    {
        await using var w = await WorldAsync();
        var first = await w.OpenAsync(Internal(1));
        var second = await w.OpenAsync(Internal(1));
        var parts = Parts(w.Db);
        var ask = Move("Issue", (Rod, 7));

        await OkAsync(parts.MoveAsync(first.Id, ask, Chef));
        var again = await OkAsync(parts.MoveAsync(first.Id, ask, Chef));
        Assert.Equal(7m, again.PartsAtJobQty);
        Assert.Equal(43m, (await w.StockAsync(Algiers, Rod)).StockQty);
        Assert.Equal(1, await w.Db.StockMovements.CountAsync(m => m.Kind == StockMovementKind.MaintenanceIssue));
        var reused = await parts.MoveAsync(second.Id, ask, Chef);
        Assert.Equal((409, "request_id_reused"), (reused.Status, reused.Code));

        var expense = new MaintenanceJobExpenseRequest("exp-1", "Soudure extérieure", 3000m, null);
        await OkAsync(parts.AddExpenseAsync(first.Id, expense, Chef, companyScoped: true));
        var twice = await OkAsync(parts.AddExpenseAsync(first.Id, expense, Chef, companyScoped: true));
        Assert.Equal(3000m, Assert.Single(twice.Expenses).Amount);
        Assert.Equal((409, "request_id_reused"), ((await parts.AddExpenseAsync(second.Id, expense, Chef, true)) is var x ? (x.Status, x.Code) : default));
    }

    // ---------- the job's life ----------

    [Fact]
    public async Task Parts_move_only_on_an_open_job_completion_keeps_them_as_used_and_cancelling_asks_for_them_back_first()
    {
        await using var w = await WorldAsync();
        var parts = Parts(w.Db);

        // Cancelling with parts at the job: refused until they are back, or said to stay as used.
        var returned = await w.OpenAsync(Internal(1));
        await OkAsync(parts.MoveAsync(returned.Id, Move("Issue", (Rod, 3)), Chef));
        var refused = await w.Jobs.CancelAsync(returned.Id, new CancelMaintenanceJobRequest("Sous-traité"), Chef);
        Assert.Equal((409, MaintenanceJobParts.PartsAtJobCode), (refused.Status, refused.Code));
        Assert.Equal("Open", (await w.Jobs.GetAsync(returned.Id)).Value!.Status);
        await OkAsync(parts.MoveAsync(returned.Id, Move("Return", (Rod, 3)), Chef));
        Assert.True((await w.Jobs.CancelAsync(returned.Id, new CancelMaintenanceJobRequest("Sous-traité"), Chef)).Ok);
        Assert.Equal(50m, (await w.StockAsync(Algiers, Rod)).StockQty);

        var kept = await w.OpenAsync(Internal(1));
        await OkAsync(parts.MoveAsync(kept.Id, Move("Issue", (Rod, 2)), Chef));
        var cancelled = await w.Jobs.CancelAsync(kept.Id, new CancelMaintenanceJobRequest("Abandonné", PartsUsed: true), Chef);
        Assert.True(cancelled.Ok, cancelled.Error);
        Assert.Contains("parts kept as used", cancelled.Value!.Events.Last().Detail);
        Assert.Equal(2m, (await OkAsync(parts.WorkAsync(kept.Id))).PartsAtJobQty);

        // Completion is the repair: what is at the job was used, and moves no more.
        var done = await w.OpenAsync(Internal(1));
        await OkAsync(parts.MoveAsync(done.Id, Move("Issue", (Rod, 4)), Chef));
        var completed = await w.Jobs.CompleteAsync(done.Id, new CompleteMaintenanceJobRequest(1, 0, null), Second);
        Assert.True(completed.Ok, completed.Error);
        Assert.Contains("parts used 4", completed.Value!.Events.Last().Detail);
        foreach (var closed in new[] { done.Id, kept.Id, returned.Id })
        {
            Assert.Equal((409, "not_live"), ((await parts.MoveAsync(closed, Move("Return", (Rod, 1)), Chef)) is var c ? (c.Status, c.Code) : default));
            Assert.Equal((409, "not_live"), ((await parts.MoveAsync(closed, Move("Issue", (Rod, 1)), Chef)) is var i ? (i.Status, i.Code) : default));
        }
        Assert.Equal(50m - 2m - 4m, (await w.StockAsync(Algiers, Rod)).StockQty);
        // Outside work arriving after the repair still belongs to it; a cancelled job takes none.
        await OkAsync(parts.AddExpenseAsync(done.Id, new MaintenanceJobExpenseRequest("late", "Peinture", 1500m, null), Chef, true));
        Assert.Equal((409, "not_live"),
            ((await parts.AddExpenseAsync(kept.Id, new MaintenanceJobExpenseRequest("late-2", "Peinture", 1m, null), Chef, true)) is var n ? (n.Status, n.Code) : default));
    }

    [Fact]
    public async Task A_draft_job_moves_no_parts_until_it_is_taken()
    {
        await using var w = await WorldAsync(On(s => s.JobsFromReturnNotes = MaintenanceReturnNoteJobs.DraftJobPerDamagedLine));
        await w.ReturnDamagedAsync(2);
        var draft = await w.Db.MaintenanceJobs.AsNoTracking().SingleAsync(j => j.Status == MaintenanceJobStatus.Draft);
        var parts = Parts(w.Db);
        Assert.Equal((409, "not_open"), ((await parts.MoveAsync(draft.Id, Move("Issue", (Rod, 1)), Chef)) is var d ? (d.Status, d.Code) : default));
        Assert.Equal((409, "not_open"),
            ((await parts.AddExpenseAsync(draft.Id, new MaintenanceJobExpenseRequest("d", "Soudure", 1m, null), Chef, true)) is var e ? (e.Status, e.Code) : default));
        Assert.True((await w.Jobs.OpenAsync(draft.Id, Second)).Ok);
        await OkAsync(parts.MoveAsync(draft.Id, Move("Issue", (Rod, 1)), Second));
    }

    // ---------- MS-32 ----------
    // "At issue" was refused until the account map's maintenance slots existed; Phase M6 built it: MaintenanceLedgerTests.

    // ---------- C-01: the weighted average ----------

    [Fact]
    public async Task Once_sales_are_costed_at_the_average_an_issue_goes_out_at_it_and_a_return_comes_back_at_what_its_issue_left_at()
    {
        await using var w = await WorldAsync();
        // One average per location. The opening carries Algiers' fifty rods at 40; fifty more bought at 60 make its average 50.
        await StockValuationStart.EnsureAsync(w.Db);
        w.Db.ChangeTracker.Clear();
        Assert.Null((await new StockValuationShadow(w.Db).UpdateSettingsAsync(
            new UpdateStockValuationSettingsRequest("Location", "SaleCost", 3), "comptable")).Error);
        w.Db.ChangeTracker.Clear();
        await ReceiveAsync(w.Db, 50m, 60m);
        var opening = await w.Db.LedgerAccounts.SingleAsync(a => a.Code == LedgerAccounts.OpeningBalance);
        w.Db.ManufacturingAccountMappings.Add(new ManufacturingAccountMapping
        {
            Slot = ManufacturingAccountSlots.OpeningAdjustment, LedgerAccountId = opening.Id, UpdatedAtUtc = DateTime.UtcNow,
        });
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        var costSwitch = new SalesCostSwitch(w.Db, new StockValuationShadow(w.Db), new LedgerPoster(w.Db));
        var status = await costSwitch.StatusAsync();
        Assert.Null((await costSwitch.SwitchAsync(new SwitchSalesCostRequest("WeightedAverage", status.Adjustment), "comptable")).Code);
        w.Db.ChangeTracker.Clear();

        var job = await w.OpenAsync(Internal(2));
        var parts = Parts(w.Db);
        await OkAsync(parts.MoveAsync(job.Id, Move("Issue", (Rod, 20)), Chef));
        var issue = await w.Db.StockMovements.AsNoTracking().SingleAsync(m => m.Kind == StockMovementKind.MaintenanceIssue);
        Assert.Equal((50m, SaleCostBasis.WeightedAverage), (issue.UnitCost!.Value, issue.CostBasis));

        // Twenty more at 80 lift the average to 56; five rods back come in at the 50 they left at, not at 56.
        await ReceiveAsync(w.Db, 20m, 80m);
        var back = await OkAsync(parts.MoveAsync(job.Id, Move("Return", (Rod, 5)), Chef));
        var ret = await w.Db.StockMovements.AsNoTracking().SingleAsync(m => m.Kind == StockMovementKind.MaintenanceReturn);
        Assert.Equal((50m, SaleCostBasis.WeightedAverage), (ret.UnitCost!.Value, ret.CostBasis));
        Assert.Equal(750m, back.PartsValue);

        // The shadow replays the return at that cost: the whole shelf (105 rods) now goes out at 4,000 + 1,600 + 250.
        var all = await OkAsync(parts.MoveAsync(job.Id, Move("Issue", (Rod, 105)), Chef));
        var last = await w.Db.MaintenanceJobParts.AsNoTracking().Where(p => p.Kind == MaintenancePartKind.Issue).OrderByDescending(p => p.AtUtc).FirstAsync();
        Assert.Equal(5850m, last.Value);
        Assert.Equal(750m + 5850m, all.PartsValue);
        Assert.Equal(0m, (await w.StockAsync(Algiers, Rod)).StockQty);
    }

    /// <summary>A receiving note at Algiers for rods: its movement and its entry, Dr 310 / Cr 401.</summary>
    private static async Task ReceiveAsync(PosDbContext db, decimal qty, decimal cost)
    {
        var movement = new StockMovement
        {
            ProductId = Rod, BranchId = Algiers, QtyDelta = qty, MovedQty = qty, Reason = "receiving", Kind = StockMovementKind.Receipt, UnitCost = cost,
        };
        db.StockMovements.Add(movement);
        (await db.ProductStocks.SingleAsync(s => s.ProductId == Rod && s.BranchId == Algiers)).StockQty += qty;
        var draft = new PostingDraft { EntryDate = DateTime.UtcNow, BranchId = Algiers, SourceType = LedgerSources.Receiving, SourceId = movement.Id };
        draft.Add(PostingLine.Dr(LedgerAccounts.Inventory, qty * cost)).Add(PostingLine.Cr(LedgerAccounts.Suppliers, qty * cost));
        await new LedgerPoster(db).PostAsync(draft);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    // ---------- outside work and the voucher cap ----------

    [Fact]
    public async Task Outside_work_takes_only_an_expense_voucher_the_caller_reaches_and_never_more_than_is_left_of_it()
    {
        await using var w = await WorldAsync();
        var job = await w.OpenAsync(Internal(1));
        var parts = Parts(w.Db);
        var algiers = new PaymentVoucher { Number = "PV-000801", BranchId = Algiers, Kind = PaymentVoucherKind.Expense, Amount = 11_000m,
            MethodKey = "cash", Status = PaymentVoucherStatus.Paid, ExpenseCategory = "repair" };
        var oran = new PaymentVoucher { Number = "PV-000802", BranchId = Oran, Kind = PaymentVoucherKind.Expense, Amount = 5000m,
            MethodKey = "cash", Status = PaymentVoucherStatus.Paid };
        var cancelled = new PaymentVoucher { Number = "PV-000803", BranchId = Algiers, Kind = PaymentVoucherKind.Expense, Amount = 5000m,
            MethodKey = "cash", Status = PaymentVoucherStatus.Cancelled };
        var refund = new PaymentVoucher { Number = "PV-000804", BranchId = Algiers, Kind = PaymentVoucherKind.CustomerRefund, Amount = 5000m,
            MethodKey = "cash", Status = PaymentVoucherStatus.Paid };
        w.Db.PaymentVouchers.AddRange(algiers, oran, cancelled, refund);
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        static MaintenanceJobExpenseRequest Work(decimal amount, Guid? voucher) => new(Guid.NewGuid().ToString("N"), "Soudeur", amount, voucher);
        var entries = await w.Db.LedgerEntries.CountAsync();

        var first = await OkAsync(parts.AddExpenseAsync(job.Id, Work(8000m, algiers.Id), Chef, companyScoped: false));
        var line = Assert.Single(first.Expenses);
        Assert.Equal((8000m, "PV-000801", "Paid"), (line.Amount, line.VoucherNumber, line.VoucherStatus));
        Assert.Equal((409, "voucher_amount_exceeded"), ((await parts.AddExpenseAsync(job.Id, Work(3000.01m, algiers.Id), Chef, false)) is var a ? (a.Status, a.Code) : default));
        await OkAsync(parts.AddExpenseAsync(job.Id, Work(3000m, algiers.Id), Chef, false));
        // A branch-held caller does not find another branch's voucher; the company's roles do.
        Assert.Equal(404, (await parts.AddExpenseAsync(job.Id, Work(100m, oran.Id), Chef, false)).Status);
        await OkAsync(parts.AddExpenseAsync(job.Id, Work(100m, oran.Id), Chef, true));
        Assert.Equal((409, "voucher_cancelled"), ((await parts.AddExpenseAsync(job.Id, Work(1m, cancelled.Id), Chef, true)) is var c ? (c.Status, c.Code) : default));
        Assert.Equal((409, "voucher_not_expense"), ((await parts.AddExpenseAsync(job.Id, Work(1m, refund.Id), Chef, true)) is var r ? (r.Status, r.Code) : default));
        Assert.Equal((400, "invalid_amount"), ((await parts.AddExpenseAsync(job.Id, Work(0m, null), Chef, true)) is var z ? (z.Status, z.Code) : default));
        // Rounded as money; an expense with no voucher is not capped.
        var free = await OkAsync(parts.AddExpenseAsync(job.Id, Work(20_000.004m, null), Chef, true));
        Assert.Equal(20_000m, free.Expenses.Last().Amount);
        Assert.Equal(8000m + 3000m + 100m + 20_000m, free.ExpensesTotal);

        // The picker: Algiers' expense vouchers with something left — none, the one at Algiers is fully linked.
        var options = (await parts.OptionsAsync(job.Id)).Value!;
        Assert.Empty(options.Vouchers);
        Assert.Contains(options.Products, p => p.Id == Rod && p.Available == 50m);
        Assert.DoesNotContain(options.Products, p => p.Id == Service);
        // The voucher posted when it was paid; linking it posts nothing.
        Assert.Equal(entries, await w.Db.LedgerEntries.CountAsync());
    }

    [Fact]
    public async Task The_voucher_cap_counts_production_orders_and_maintenance_jobs_together_and_production_reservations_hold()
    {
        await using var s = await MfgHarness.NewAsync();
        var factory = await ProductionWorld.SetupAsync(s.Db);
        // A job at line A's warehouse, over two units of nails in maintenance there; the module on.
        s.Db.ProductStocks.Add(new ProductStock { ProductId = factory.Nails, BranchId = MfgHarness.WarehouseA, MaintenanceQty = 2m });
        s.Db.MaintenanceSettings.Add(new MaintenanceSettings { Module = MaintenanceSwitch.On });
        var voucher = new PaymentVoucher { Number = "PV-000901", BranchId = MfgHarness.WarehouseA, Kind = PaymentVoucherKind.Expense, Amount = 10_000m,
            MethodKey = "cash", Status = PaymentVoucherStatus.Paid };
        s.Db.PaymentVouchers.Add(voucher);
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var jobs = new MaintenanceJobs(s.Db, new LedgerPoster(s.Db), MaintenanceWorld.Regional);
        var opened = await jobs.CreateAsync(new CreateMaintenanceJobRequest("mj-1", MfgHarness.WarehouseA, "Internal", factory.Nails, 2m, null, null, null), Chef);
        Assert.True(opened.Ok, opened.Error);
        var job = opened.Value!.Id;
        var parts = Parts(s.Db);

        // An order of 100 kg claims 105 kg of the 300 kg of wire at the warehouse: a job takes only the rest.
        var order = ProductionWorld.OrderId(await factory.CreateOrderAsync(s.Db, factory.LineA, 100));
        var claimed = await parts.MoveAsync(job, Move("Issue", (factory.Wire, 196m)), Chef);
        Assert.Equal((409, "reserved_by_production"), (claimed.Status, claimed.Code));
        Assert.Equal(195m, (await parts.OptionsAsync(job)).Value!.Products.Single(p => p.Id == factory.Wire).Available);
        await OkAsync(parts.MoveAsync(job, Move("Issue", (factory.Wire, 195m)), Chef));

        // One voucher of 10,000: 6,000 to the order, so the job may take 4,000 and not a centime more — and then the order neither.
        const string path = "/api/manufacturing/orders/{id:guid}/expenses";
        static string OrderExpense(decimal amount, Guid voucher) => JsonSerializer.Serialize(
            new { description = "Galvanisation", amount, paymentVoucherId = voucher, clientRequestId = Guid.NewGuid().ToString("N") });
        await MfgHarness.OkAsync(s.Db, factory.AdminPrincipal, "POST", path, OrderExpense(6000m, voucher.Id), ProductionWorld.Of(order));
        var over = await parts.AddExpenseAsync(job, new MaintenanceJobExpenseRequest("j-1", "Soudure", 4000.01m, voucher.Id), Chef, true);
        Assert.Equal((409, "voucher_amount_exceeded"), (over.Status, over.Code));
        Assert.Equal(4000m, (await parts.OptionsAsync(job)).Value!.Vouchers.Single().Free);
        await OkAsync(parts.AddExpenseAsync(job, new MaintenanceJobExpenseRequest("j-2", "Soudure", 4000m, voucher.Id), Chef, true));
        await MfgHarness.RefusedAsync(s.Db, factory.AdminPrincipal, 409, "voucher_amount_exceeded", "POST", path, OrderExpense(0.01m, voucher.Id),
            ProductionWorld.Of(order));
        Assert.Empty((await parts.OptionsAsync(job)).Value!.Vouchers);
    }

    // ---------- the routes ----------

    [Fact]
    public async Task The_routes_answer_409_with_the_module_off_and_404_for_another_branch_s_job()
    {
        await using var w = await WorldAsync();
        var job = await w.OpenAsync(Internal(1));
        var id = ("id", job.Id.ToString());
        var algiersManager = await w.CallerAsync(UserRole.Manager, Algiers, Chef);
        var oranManager = await w.CallerAsync(UserRole.Manager, Oran, Second);
        var issue = Json(Move("Issue", (Rod, 2)));
        var expense = Json(new MaintenanceJobExpenseRequest("route-1", "Soudure", 500m, null));

        foreach (var (method, pattern, body) in new[]
        {
            ("GET", "/api/maintenance/jobs/{id:guid}/work", ""), ("GET", "/api/maintenance/jobs/{id:guid}/work-options", ""),
            ("POST", "/api/maintenance/jobs/{id:guid}/parts", issue), ("POST", "/api/maintenance/jobs/{id:guid}/expenses", expense),
        })
            Assert.Equal(404, (await w.CallAsync(oranManager, method, pattern, body, id)).Status);

        var issued = await w.CallAsync(algiersManager, "POST", "/api/maintenance/jobs/{id:guid}/parts", issue, id);
        Assert.Equal(200, issued.Status);
        Assert.Equal(2m, JsonSerializer.Deserialize<MaintenanceJobWorkDto>(issued.Body, Web)!.PartsAtJobQty);
        Assert.Equal(200, (await w.CallAsync(algiersManager, "POST", "/api/maintenance/jobs/{id:guid}/expenses", expense, id)).Status);
        var work = JsonSerializer.Deserialize<MaintenanceJobWorkDto>((await w.CallAsync(algiersManager, "GET", "/api/maintenance/jobs/{id:guid}/work", "", id)).Body, Web)!;
        Assert.Equal((2m, 500m), (work.PartsAtJobQty, work.ExpensesTotal));

        await w.Db.MaintenanceSettings.ExecuteUpdateAsync(x => x.SetProperty(r => r.Module, MaintenanceSwitch.Off));
        var off = await w.CallAsync(algiersManager, "POST", "/api/maintenance/jobs/{id:guid}/parts", Json(Move("Issue", (Rod, 1))), id);
        Assert.Equal((409, MaintenanceJobs.OffCode), (off.Status, CodeOf(off.Body)));
        Assert.Equal(48m, (await w.StockAsync(Algiers, Rod)).StockQty);
    }
}
