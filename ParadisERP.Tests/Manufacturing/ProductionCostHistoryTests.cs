using System.Text.Json;
using ParadisERP.Application.Manufacturing;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Tests.Security;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Manufacturing.CostHistoryWorld;
using static ParadisERP.Tests.Manufacturing.MfgHarness;
using static ParadisERP.Tests.Manufacturing.ProductionWorld;

namespace ParadisERP.Tests.Manufacturing;

/// <summary>
/// Manufacturing costing B2 on SQLite (docs/manufacturing/costing-b2.md): the reconciliation of B1's frozen sheets,
/// through its seven read-only routes — figures, B1's own allocation rules, late-cost treatment, integrity, scope,
/// redaction, exports, and that nothing is ever written.
/// </summary>
public sealed class ProductionCostHistoryTests
{
    private static string Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTime.UtcNow,
        ParadisERP.Infrastructure.Catalog.StoreDocumentHistory.Zone(new Algeria()))).ToString("yyyy-MM-dd");

    // ---------- figures ----------

    [Fact]
    public async Task The_original_stays_frozen_and_only_recognized_late_effects_reach_the_adjusted_provisional_total()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var id = await WorkedAndCompletedAsync(s.Db, w);
        var frozen = await FreezeAsync(s.Db, w, id);
        var original = frozen.GetProperty("totalCost").GetDecimal();

        await LateCostAsync(s.Db, w, id, "expense", "PV-900", 120m);          // recognized, then doubted by its twin below
        await LateCostAsync(s.Db, w, id, "labour", "PAY-2026-10", -30m);      // recognized
        await LateCostAsync(s.Db, w, id, "freight-fix", "INV-77", 50m);       // unclassified
        await LateCostAsync(s.Db, w, id, "expenses", "pv-900 ", 120m);        // the same source for the same element: uncertain

        var r = await OkAsync(s.Db, w.AdminPrincipal, "GET", Reconciliation, "", Of(id));
        var f = r.GetProperty("figures");
        Assert.Equal("frozen", r.GetProperty("state").GetString());
        Assert.Equal((original, 260m, -30m, 50m, 240m, original - 30m),
            (Dec(f, "original"), Dec(f, "lateRecorded"), Dec(f, "lateRecognized"), Dec(f, "lateUnclassified"), Dec(f, "lateUncertain"), Dec(f, "adjustedProvisional")));
        Assert.Equal(JsonValueKind.Null, f.GetProperty("financiallyFinalized").ValueKind);
        Assert.Equal("not_implemented", f.GetProperty("finalizationState").GetString());

        var rows = r.GetProperty("lateCosts").EnumerateArray().ToList();
        Assert.Equal(4, rows.Count);
        Assert.All(rows.Where(x => x.GetProperty("treatment").GetString() == "uncertain"),
            x => Assert.Contains("duplicate_source", x.GetProperty("flags").EnumerateArray().Select(v => v.GetString())));
        var unclassified = Find(r.GetProperty("lateCosts"), "kind", "freight-fix");
        Assert.Equal(("unclassified", JsonValueKind.Null, 50m, 0m), (unclassified.GetProperty("treatment").GetString(),
            unclassified.GetProperty("element").ValueKind, Dec(unclassified, "unclassifiedEffect"), Dec(unclassified, "recognizedEffect")));
        Assert.Equal(-30m, Dec(Find(r.GetProperty("lateCosts"), "kind", "labour"), "recognizedEffect"));

        // Elements add up: Σ adjusted lines + residual = the adjusted provisional total; the late labour sits on labour.
        var elements = r.GetProperty("elements").EnumerateArray().ToList();
        Assert.Equal(7, elements.Count);
        Assert.Equal(Dec(f, "adjustedProvisional"), elements.Sum(e => Dec(e, "adjusted")) + Dec(r, "roundingResidual"));
        var labour = Find(r.GetProperty("elements"), "element", "labour");
        Assert.Equal(Dec(labour, "original") - 30m, Dec(labour, "adjusted"));
        Assert.Equal("fail", Result(r, CostReconciliation.CheckLateSources));

        // The sheet itself is untouched.
        Assert.Equal(original, (await s.Db.ManufacturingCostSheets.AsNoTracking().SingleAsync(x => x.OrderId == id)).TotalCost);
    }

    [Fact]
    public async Task A_frozen_sheet_reconciles_by_B1s_own_rules_and_its_source_replays_exactly()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var id = await WorkedAndCompletedAsync(s.Db, w);
        await FreezeAsync(s.Db, w, id);
        var live = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/orders/{id:guid}/costing", "", Of(id));

        var r = await OkAsync(s.Db, w.AdminPrincipal, "GET", Reconciliation, "", Of(id));
        foreach (var check in new[] { "source_complete", "source_hash", "source_replay", "element_identity", "material_rows",
                     "expense_rows", "labour_rows", "scrap_row", "release_sum", "unit_cost" })
            Assert.True(Result(r, check) == "pass", $"{check}: {r.GetProperty("checks")}");
        Assert.Equal("not_applicable", Result(r, "late_sources"));
        Assert.Equal(0m, Dec(r, "roundingResidual"));

        // Releases in the engine's order; the last carries the remainder, so they add up to the original exactly.
        var rel = r.GetProperty("releases");
        var lines = rel.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal((60m, 38m), (Dec(lines[0], "quantity"), Dec(lines[1], "quantity")));
        Assert.Equal((false, true), (lines[0].GetProperty("carriesRemainder").GetBoolean(), lines[1].GetProperty("carriesRemainder").GetBoolean()));
        Assert.Equal(Dec(r, "figures", "original"), lines.Sum(l => Dec(l, "value")));
        Assert.Equal((98m, 0m, 0m), (Dec(rel, "releasedQty"), Dec(rel, "unreleasedAcceptedQty"), Dec(rel, "keptInCharges")));
        Assert.Empty(rel.GetProperty("flags").EnumerateArray());
        Assert.Equal(live.GetProperty("releases")[0].GetProperty("value").GetDecimal(), Dec(lines[0], "value"));

        // Rejects absorbed (D23): 2 of 100 — spoilage is part of C, the recovery is not recorded, never 0-as-a-value.
        var scrap = r.GetProperty("scrap");
        Assert.Equal((98m, 2m, 2.00m), (Dec(scrap, "acceptedQty"), Dec(scrap, "rejectedQty"), Dec(scrap, "rejectionRatePercent")));
        Assert.Equal(CostReconciliation.Money(Dec(r, "figures", "original") * 2m / 100m), Dec(scrap, "spoilageAbsorbed"));
        Assert.Equal("not_recorded", scrap.GetProperty("recoveryStatus").GetString());
        Assert.Equal(live.GetProperty("materials").EnumerateArray().Sum(m => Dec(m, "scrapValue")), Dec(scrap, "materialScrapValue"));

        // The variances replayed from the frozen inputs are the engine's, and their lines add up to the total.
        var variance = r.GetProperty("variances");
        Assert.True(variance.GetProperty("available").GetBoolean());
        var vlines = variance.GetProperty("lines").EnumerateArray().ToList();
        var total = vlines.Single(l => l.GetProperty("component").GetString() == "total");
        Assert.Equal(Dec(live, "variances", "total"), Dec(total, "original"));
        Assert.Equal(Dec(total, "original"), vlines.Where(l => !l.GetProperty("detail").GetBoolean() && l.GetProperty("component").GetString() != "total")
            .Sum(l => Dec(l, "original")));
    }

    [Fact]
    public async Task As_of_counts_late_costs_by_their_record_time_on_the_company_day()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var id = await WorkedAndCompletedAsync(s.Db, w);
        await FreezeAsync(s.Db, w, id);
        var today = DateOnly.Parse(Today(), System.Globalization.CultureInfo.InvariantCulture);
        // Africa/Algiers is UTC+1: the company day ends at 23:00 UTC.
        var lastMoment = today.ToDateTime(new TimeOnly(22, 59, 59), DateTimeKind.Utc);
        await InsertLateAsync(s.Db, id, "expense", "LATE-IN", 10m, lastMoment);
        await InsertLateAsync(s.Db, id, "expense", "LATE-OUT", 7m, lastMoment.AddSeconds(1));

        var r = await OkAsync(s.Db, w.AdminPrincipal, "GET", Reconciliation, $"?asOf={Today()}", Of(id));
        Assert.Equal(10m, Dec(r, "figures", "lateRecognized"));
        Assert.Single(r.GetProperty("lateCosts").EnumerateArray());
        var next = await OkAsync(s.Db, w.AdminPrincipal, "GET", Reconciliation, $"?asOf={today.AddDays(1):yyyy-MM-dd}", Of(id));
        Assert.Equal(17m, Dec(next, "figures", "lateRecognized"));

        // Before the sheet was made, the order was not frozen: no figures are invented for that day.
        var before = await OkAsync(s.Db, w.AdminPrincipal, "GET", Reconciliation, $"?asOf={today.AddDays(-1):yyyy-MM-dd}", Of(id));
        Assert.Contains(before.GetProperty("state").GetString(), new[] { "not_frozen_at_date", "not_completed" });
        Assert.Equal(JsonValueKind.Null, before.GetProperty("figures").ValueKind);
    }

    [Fact]
    public async Task A_completed_order_without_a_snapshot_is_missing_never_a_zero_cost()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var missing = await EmptyCompletedAsync(s.Db, w);
        var zero = await EmptyCompletedAsync(s.Db, w);
        await FreezeAsync(s.Db, w, zero);
        var open = OrderId(await w.CreateOrderAsync(s.Db, w.LineA, 50m));

        var history = await OkAsync(s.Db, w.AdminPrincipal, "GET", History);
        var none = history.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("orderId").GetGuid() == missing);
        Assert.Equal(("not_frozen", JsonValueKind.Null, JsonValueKind.Null), (none.GetProperty("state").GetString(),
            none.GetProperty("figures").ValueKind, none.GetProperty("sheet").ValueKind));
        var zeroRow = history.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("orderId").GetGuid() == zero);
        Assert.Equal(("frozen", 0m), (zeroRow.GetProperty("state").GetString(), Dec(zeroRow, "figures", "original")));
        Assert.Contains("zero_cost", zeroRow.GetProperty("gapCodes").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal((1, 1), (history.GetProperty("totals").GetProperty("frozen").GetInt32(), history.GetProperty("totals").GetProperty("notFrozen").GetInt32()));
        Assert.DoesNotContain(history.GetProperty("rows").EnumerateArray(), r => r.GetProperty("orderId").GetGuid() == open);

        var gaps = await OkAsync(s.Db, w.AdminPrincipal, "GET", Gaps, "?severity=missing");
        var gap = gaps.GetProperty("rows").EnumerateArray().Single();
        Assert.Equal((missing, "not_frozen"), (gap.GetProperty("orderId").GetGuid(), gap.GetProperty("gaps")[0].GetProperty("code").GetString()));

        Assert.Equal("not_frozen", (await OkAsync(s.Db, w.AdminPrincipal, "GET", Reconciliation, "", Of(missing))).GetProperty("state").GetString());
        Assert.Equal("not_completed", (await OkAsync(s.Db, w.AdminPrincipal, "GET", Reconciliation, "", Of(open))).GetProperty("state").GetString());
        // Missing snapshots are listed only when asked for all or not frozen.
        var frozenOnly = await OkAsync(s.Db, w.AdminPrincipal, "GET", History, "?state=frozen");
        Assert.All(frozenOnly.GetProperty("rows").EnumerateArray(), r => Assert.Equal("frozen", r.GetProperty("state").GetString()));
    }

    // ---------- integrity ----------

    [Fact]
    public async Task Integrity_checks_detect_edits_made_outside_the_application_and_never_repair_them()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var hashed = await WorkedAndCompletedAsync(s.Db, w);
        await FreezeAsync(s.Db, w, hashed);
        var rowEdited = await WorkedAndCompletedAsync(s.Db, w, dayOffset: 1);
        await FreezeAsync(s.Db, w, rowEdited);
        var hashedSheet = (await s.Db.ManufacturingCostSheets.AsNoTracking().SingleAsync(x => x.OrderId == hashed)).Id;
        var editedSheet = (await s.Db.ManufacturingCostSheets.AsNoTracking().SingleAsync(x => x.OrderId == rowEdited)).Id;

        // A privileged editor bypassing EF: the capture gains a byte, an element line a new value.
        await s.Db.Database.ExecuteSqlRawAsync("UPDATE ManufacturingCostSheets SET SourceSnapshotJson = SourceSnapshotJson || ' ' WHERE Id = {0}", hashedSheet);
        await s.Db.Database.ExecuteSqlRawAsync("UPDATE ManufacturingCostElementLines SET TotalCost = '1.000' WHERE SheetId = {0} AND Element = 'materials'", editedSheet);
        var before = await FingerprintAsync(s.Db);

        var first = await OkAsync(s.Db, w.AdminPrincipal, "GET", Reconciliation, "", Of(hashed));
        Assert.Equal(("fail", "pass"), (Result(first, "source_hash"), Result(first, "source_replay")));
        var second = await OkAsync(s.Db, w.AdminPrincipal, "GET", Reconciliation, "", Of(rowEdited));
        Assert.Equal(("pass", "fail", "fail", "fail"), (Result(second, "source_hash"), Result(second, "source_replay"),
            Result(second, "element_identity"), Result(second, "material_rows")));

        var gaps = await OkAsync(s.Db, w.AdminPrincipal, "GET", Gaps, "?code=source_hash");
        Assert.Equal(hashed, gaps.GetProperty("rows").EnumerateArray().Single().GetProperty("orderId").GetGuid());
        var history = await OkAsync(s.Db, w.AdminPrincipal, "GET", History);
        Assert.Equal(2, history.GetProperty("totals").GetProperty("integrityFailures").GetInt32());
        // Diagnostics only: the edited rows are exactly as the editor left them.
        Assert.Equal(before, await FingerprintAsync(s.Db));
    }

    [Fact]
    public async Task Reconstructed_and_legacy_sheets_say_what_they_cannot_prove()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var late = await WorkedAndCompletedAsync(s.Db, w);
        var order = await s.Db.ProductionOrders.SingleAsync(o => o.Id == late);
        order.CompletedAtUtc = order.CompletedAtUtc!.Value.AddDays(-9);
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        await FreezeAsync(s.Db, w, late, reconstruct: true);

        var r = await OkAsync(s.Db, w.AdminPrincipal, "GET", Reconciliation, "", Of(late));
        Assert.Equal(("reconstructed", 9), (r.GetProperty("sheet").GetProperty("kind").GetString(), r.GetProperty("sheet").GetProperty("freezeLagDays").GetInt32()));
        Assert.Equal(9m, Dec(Find(r.GetProperty("gaps"), "code", "reconstructed"), "value"));

        // A pre-capture B1 row: no source, so no hash, replay or variance — and its completeness fails, in words.
        var legacy = await EmptyCompletedAsync(s.Db, w);
        var legacyOrder = await s.Db.ProductionOrders.AsNoTracking().SingleAsync(o => o.Id == legacy);
        var sheet = new ManufacturingCostSheet
        {
            OrderId = legacy, LineId = legacyOrder.LineId, Kind = ManufacturingCostSnapshotKind.Created, RequestId = "legacy-b1",
            SourceState = ManufacturingCostSourceState.IncompleteLegacy, AcceptedBaseQty = 100m, TotalCost = 0m, UnitCost = 0m,
        };
        foreach (var e in CostReconciliation.Elements)
            sheet.Elements.Add(new ManufacturingCostElementLine { SheetId = sheet.Id, Element = e, Quantity = 1m, TotalCost = 0m, Status = "none" });
        s.Db.ManufacturingCostSheets.Add(sheet);
        s.Db.ManufacturingScrapRecoverySnapshots.Add(new ManufacturingScrapRecoverySnapshot { SheetId = sheet.Id });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        var l = await OkAsync(s.Db, w.AdminPrincipal, "GET", Reconciliation, "", Of(legacy));
        Assert.Equal("incompletelegacy", l.GetProperty("sheet").GetProperty("sourceState").GetString());
        Assert.Equal(("fail", "not_applicable", "not_applicable", "pass"),
            (Result(l, "source_complete"), Result(l, "source_hash"), Result(l, "source_replay"), Result(l, "element_identity")));
        Assert.False(l.GetProperty("variances").GetProperty("available").GetBoolean());
        Assert.Equal("source_not_captured", l.GetProperty("variances").GetProperty("reason").GetString());
        Assert.Contains("source_incomplete_legacy", l.GetProperty("gaps").EnumerateArray().Select(g => g.GetProperty("code").GetString()));
    }

    // ---------- late-cost sources ----------

    [Fact]
    public async Task Case_variant_and_already_frozen_sources_are_uncertain_and_shared_sources_are_flagged()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var voucher = new PaymentVoucher { Number = "PV-004411", BranchId = TillHarness.Algiers, Kind = PaymentVoucherKind.Expense, Amount = 5_000m,
            MethodKey = "cash", Status = PaymentVoucherStatus.Approved };
        s.Db.PaymentVouchers.Add(voucher);
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var id = await WorkedAndCompletedAsync(s.Db, w, expense: 0m);
        await ExpenseAsync(s.Db, w, id, 800m, voucher.Id);
        await FreezeAsync(s.Db, w, id);
        var other = await WorkedAndCompletedAsync(s.Db, w, dayOffset: 1);
        await FreezeAsync(s.Db, w, other);

        await LateCostAsync(s.Db, w, id, "expense", "PV-777", 15m);
        await LateCostAsync(s.Db, w, id, "expense", "pv-777", 15m);      // SQLite keeps both: a case-only twin
        await LateCostAsync(s.Db, w, id, "expense", "voucher: pv-004411", 40m); // names a voucher already frozen in the sheet
        await LateCostAsync(s.Db, w, id, "labour", "WO-5", 5m);
        await LateCostAsync(s.Db, w, id, "overhead", "WO-5", 6m);        // one document for two elements: shared, recognized
        await LateCostAsync(s.Db, w, other, "expense", "INV-SHARED", 9m);
        await LateCostAsync(s.Db, w, id, "consumable", "INV-SHARED", 3m); // the same reference on another order

        var report = await OkAsync(s.Db, w.AdminPrincipal, "GET", LateCosts);
        var rows = report.GetProperty("rows").EnumerateArray().ToList();
        JsonElement RowOf(string source, Guid order, string? kind = null) => rows.Single(r => r.GetProperty("sourceReference").GetString() == source
            && r.GetProperty("orderId").GetGuid() == order && (kind is null || r.GetProperty("kind").GetString() == kind));
        string Treat(string source, Guid order, string? kind = null) => RowOf(source, order, kind).GetProperty("treatment").GetString()!;
        string[] Flags(string source, Guid order, string? kind = null) =>
            RowOf(source, order, kind).GetProperty("flags").EnumerateArray().Select(f => f.GetString()!).ToArray();
        Assert.Equal(("uncertain", "uncertain", "uncertain"), (Treat("PV-777", id), Treat("pv-777", id), Treat("voucher: pv-004411", id)));
        Assert.Contains("duplicate_source", Flags("PV-777", id));
        Assert.Contains("overlaps_original", Flags("voucher: pv-004411", id));
        Assert.Equal(("recognized", "recognized"), (Treat("WO-5", id, "labour"), Treat("WO-5", id, "overhead")));
        Assert.Contains("shared_source", Flags("WO-5", id, "labour"));
        Assert.Contains("source_on_several_orders", Flags("INV-SHARED", other));
        Assert.Equal("recognized", Treat("INV-SHARED", other));

        // Totals add each record once, by treatment.
        Assert.Equal((93m, 23m, 0m, 70m), (Dec(report, "recorded"), Dec(report, "recognized"), Dec(report, "unclassified"), Dec(report, "uncertain")));
        var filtered = await OkAsync(s.Db, w.AdminPrincipal, "GET", LateCosts, "?flag=duplicate_source");
        Assert.Equal(2, filtered.GetProperty("rows").GetArrayLength());
    }

    // ---------- scope, permissions and redaction ----------

    [Fact]
    public async Task Every_route_needs_the_switch_and_costs_view_and_hides_other_lines()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var id = await WorkedAndCompletedAsync(s.Db, w);
        await FreezeAsync(s.Db, w, id);

        var (_, noCosts) = await ManagerAsync(s.Db, WarehouseA, AllGrants.Where(g => g != PermissionCatalog.ManufacturingCostsView).ToArray());
        await RefusedAsync(s.Db, noCosts, 403, "forbidden", "GET", Reconciliation, "", Of(id));
        await RefusedAsync(s.Db, noCosts, 403, "forbidden", "GET", Reconciliation, "?format=xlsx", Of(id));
        foreach (var route in Registers)
        {
            await RefusedAsync(s.Db, noCosts, 403, "forbidden", "GET", route);
            await RefusedAsync(s.Db, noCosts, 403, "forbidden", "GET", route, "?format=xlsx");
        }

        Costing = false;
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "costing_not_enabled", "GET", Reconciliation, "", Of(id));
        foreach (var route in Registers) await RefusedAsync(s.Db, w.AdminPrincipal, 409, "costing_not_enabled", "GET", route);
        Costing = true;

        // Another branch's manager, with pay-rate access (the history is labour by order): the order is not found,
        // exactly as an id that does not exist.
        var (_, otherLine) = await ManagerAsync(s.Db, WarehouseB, [.. AllGrants, PermissionCatalog.PayrollManage]);
        var foreign = await CallAsync(s.Db, otherLine, "GET", Reconciliation, "", Of(id));
        var nowhere = await CallAsync(s.Db, otherLine, "GET", Reconciliation, "", Of(Guid.NewGuid()));
        Assert.Equal((404, nowhere.Body), (foreign.Status, foreign.Body));
        foreach (var route in Registers)
        {
            var report = await OkAsync(s.Db, otherLine, "GET", route);
            Assert.DoesNotContain(id.ToString(), report.GetRawText());
            // Filtering on a line out of reach selects nothing, never a 404 that would confirm it exists.
            var filtered = await OkAsync(s.Db, otherLine, "GET", route, $"?lineId={w.LineA}");
            Assert.Equal(0, filtered.GetProperty("page").GetProperty("total").GetInt32());
        }
        var history = await OkAsync(s.Db, otherLine, "GET", History);
        Assert.Equal((0, 0m), (history.GetProperty("totals").GetProperty("frozen").GetInt32(), Dec(history, "totals", "original")));
        Assert.Equal(400, (await CallAsync(s.Db, w.AdminPrincipal, "GET", History, "?from=2026-01-01&to=2027-06-01")).Status);
        Assert.Equal(400, (await CallAsync(s.Db, w.AdminPrincipal, "GET", History, "?format=pdf")).Status);
    }

    [Fact]
    public async Task An_order_s_frozen_sheet_keeps_its_totals_release_values_and_late_amounts_from_a_reader_without_rates()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var id = await WorkedAndCompletedAsync(s.Db, w);
        await FreezeAsync(s.Db, w, id);
        await LateCostAsync(s.Db, w, id, "expense", "PV-FREIGHT-9", 20m, "Freight invoice");
        var (_, costsOnly) = await ManagerAsync(s.Db, WarehouseA, AllGrants.Where(g => g != PermissionCatalog.ManufacturingCostsRatesView).ToArray());
        var (_, both) = await ManagerAsync(s.Db, WarehouseA, [.. AllGrants, PermissionCatalog.PayrollManage]);

        // Without the rate keys: the frozen total less its materials is its labour, so the total, the unit cost, the
        // releases' values and the late costs' amounts stay out; what was frozen is untouched.
        var hidden = await OkAsync(s.Db, costsOnly, "GET", CostHistoryWorld.Snapshot, "", Of(id));
        Assert.True(hidden.GetProperty("orderMoneyWithheld").GetBoolean());
        Assert.Equal((JsonValueKind.Null, JsonValueKind.Null), (hidden.GetProperty("totalCost").ValueKind, hidden.GetProperty("unitCost").ValueKind));
        Assert.All(hidden.GetProperty("releases").EnumerateArray(), r => Assert.Equal(JsonValueKind.Null, r.GetProperty("value").ValueKind));
        var late = Assert.Single(hidden.GetProperty("lateCosts").EnumerateArray());
        Assert.Equal((JsonValueKind.Null, "PV-FREIGHT-9"), (late.GetProperty("amount").ValueKind, late.GetProperty("sourceReference").GetString()));

        var shown = await OkAsync(s.Db, both, "GET", CostHistoryWorld.Snapshot, "", Of(id));
        Assert.False(shown.GetProperty("orderMoneyWithheld").GetBoolean());
        Assert.Equal(JsonValueKind.Number, shown.GetProperty("totalCost").ValueKind);
        Assert.Equal(20m, Dec(Assert.Single(shown.GetProperty("lateCosts").EnumerateArray()), "amount"));
        Assert.Equal(shown.GetProperty("acceptedBaseQty").GetDecimal(), hidden.GetProperty("acceptedBaseQty").GetDecimal());
        Assert.Equal(shown.GetProperty("sourceHash").GetString(), hidden.GetProperty("sourceHash").GetString());
    }

    [Fact]
    public async Task The_cost_history_needs_pay_rate_access_whole_and_exports_too_and_shows_worker_details_with_it()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var shared = await WorkedAndCompletedAsync(s.Db, w);              // Karim and Samir
        var single = await WorkedAndCompletedAsync(s.Db, w, twoWorkers: false, dayOffset: 1); // Karim alone
        foreach (var id in new[] { shared, single }) await FreezeAsync(s.Db, w, id);
        await LateCostAsync(s.Db, w, single, "labour", "PAY-KARIM-2026-10", 64m, "Karim's October payslip approved");
        await LateCostAsync(s.Db, w, single, "salary-fix", "PAY-KARIM-BONUS", 11m, "Karim's bonus");
        await LateCostAsync(s.Db, w, single, "expense", "PV-FREIGHT-9", 20m, "Freight invoice");

        var withoutRates = AllGrants.Where(g => g != PermissionCatalog.ManufacturingCostsRatesView).ToArray();
        var (_, costsOnly) = await ManagerAsync(s.Db, WarehouseA, withoutRates);
        var (_, ratesNoPayroll) = await ManagerAsync(s.Db, WarehouseA, AllGrants);
        var (_, payrollNoRates) = await ManagerAsync(s.Db, WarehouseA, [.. withoutRates, PermissionCatalog.PayrollManage]);
        var (_, both) = await ManagerAsync(s.Db, WarehouseA, [.. AllGrants, PermissionCatalog.PayrollManage]);

        // Without both keys the history is refused whole, every register and every export: it is labour by order, and
        // an order's frozen total less its materials is its labour (owner, 2026-11-03).
        foreach (var hidden in new[] { costsOnly, ratesNoPayroll, payrollNoRates })
        {
            await RefusedAsync(s.Db, hidden, 403, "payroll_details_withheld", "GET", Reconciliation, "?live=true", Of(single));
            await RefusedAsync(s.Db, hidden, 403, "payroll_details_withheld", "GET", Reconciliation, "?format=xlsx&lang=fr", Of(shared));
            foreach (var route in Registers)
            {
                await RefusedAsync(s.Db, hidden, 403, "payroll_details_withheld", "GET", route);
                await RefusedAsync(s.Db, hidden, 403, "payroll_details_withheld", "GET", route, "?format=xlsx&lang=en");
            }
        }

        var full = await OkAsync(s.Db, both, "GET", Reconciliation, "", Of(single));
        Assert.True(full.GetProperty("labour").GetProperty("perEmployeeMoneyShown").GetBoolean());
        Assert.Equal(8m, Dec(full.GetProperty("labour").GetProperty("rows")[0], "hours"));
        Assert.NotEqual(JsonValueKind.Null, full.GetProperty("labour").GetProperty("rows")[0].GetProperty("rate").ValueKind);
        Assert.Equal("PAY-KARIM-2026-10", Find(full.GetProperty("lateCosts"), "kind", "labour").GetProperty("sourceReference").GetString());
        Assert.Contains("labour_rate", full.GetProperty("variances").GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("component").GetString()));
        Assert.Contains("PAY-KARIM", WorkbookText((await CallRawAsync(s.Db, both, "GET", LateCosts, "?format=xlsx")).Body));

        // The capture and payroll's own figures never leave the server, whoever asks.
        foreach (var caller in new[] { costsOnly, both, w.AdminPrincipal })
        {
            var bodies = new List<string> { (await CallAsync(s.Db, caller, "GET", Reconciliation, "?live=true", Of(single))).Body };
            foreach (var route in Registers) bodies.Add((await CallAsync(s.Db, caller, "GET", route, route == History ? "?live=true" : "")).Body);
            foreach (var body in bodies)
            {
                Assert.DoesNotContain("SourceSnapshotJson", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("employerCost", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("paidHours", body, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task Arabic_exports_are_right_to_left_and_finalized_is_words_never_a_number()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var id = await WorkedAndCompletedAsync(s.Db, w);
        await FreezeAsync(s.Db, w, id);
        foreach (var route in Registers)
        {
            var (status, xlsx, _) = await CallRawAsync(s.Db, w.AdminPrincipal, "GET", route, "?format=xlsx&lang=ar");
            Assert.True(status == 200, route);
            Assert.Contains("rightToLeft=\"1\"", WorkbookText(xlsx));
        }
        var history = WorkbookText((await CallRawAsync(s.Db, w.AdminPrincipal, "GET", History, "?format=xlsx&lang=fr")).Body);
        Assert.Contains("Non disponible", history);
        Assert.Contains("Chiffres provisoires", history);
        var order = WorkbookText((await CallRawAsync(s.Db, w.AdminPrincipal, "GET", Reconciliation, "?format=xlsx&lang=en", Of(id))).Body);
        Assert.Contains("Not available", order);
    }

    // ---------- read only ----------

    [Fact]
    public async Task B2_writes_nothing_on_any_route_or_export()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var id = await WorkedAndCompletedAsync(s.Db, w);
        await FreezeAsync(s.Db, w, id);
        await LateCostAsync(s.Db, w, id, "expense", "PV-1", 10m);
        await ExpenseAsync(s.Db, w, id, 75m);
        var before = await FingerprintAsync(s.Db);

        await OkAsync(s.Db, w.AdminPrincipal, "GET", Reconciliation, "?live=true", Of(id));
        await CallRawAsync(s.Db, w.AdminPrincipal, "GET", Reconciliation, "?live=true&format=xlsx", Of(id));
        foreach (var route in Registers)
        {
            await OkAsync(s.Db, w.AdminPrincipal, "GET", route, route == History ? "?live=true" : "");
            await CallRawAsync(s.Db, w.AdminPrincipal, "GET", route, "?format=xlsx");
        }
        Assert.Equal(before, await FingerprintAsync(s.Db));
    }

    // ---------- live comparison (B2.4) ----------

    [Fact]
    public async Task The_live_comparison_shows_drift_only_late_costs_explain_and_creates_nothing()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var voucher = new PaymentVoucher { Number = "PV-005500", BranchId = TillHarness.Algiers, Kind = PaymentVoucherKind.Expense, Amount = 5_000m,
            MethodKey = "cash", Status = PaymentVoucherStatus.Pending };
        s.Db.PaymentVouchers.Add(voucher);
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var id = await WorkedAndCompletedAsync(s.Db, w, expense: 0m);
        await ExpenseAsync(s.Db, w, id, 300m, voucher.Id);
        await FreezeAsync(s.Db, w, id);

        var unchanged = (await OkAsync(s.Db, w.AdminPrincipal, "GET", Reconciliation, "?live=true", Of(id))).GetProperty("live");
        Assert.True(unchanged.GetProperty("comparable").GetBoolean());
        Assert.Equal(0m, Dec(unchanged, "unexplained"));
        Assert.Empty(unchanged.GetProperty("sourceChanges").EnumerateArray());

        // After the freeze: an expense still recorded on the completed order, and the voucher cancelled.
        await ExpenseAsync(s.Db, w, id, 200m);
        (await s.Db.PaymentVouchers.SingleAsync(v => v.Id == voucher.Id)).Status = PaymentVoucherStatus.Cancelled;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var sheets = await s.Db.ManufacturingCostSheets.CountAsync();
        var lates = await s.Db.ManufacturingLateCosts.CountAsync();

        var drift = (await OkAsync(s.Db, w.AdminPrincipal, "GET", Reconciliation, "?live=true", Of(id))).GetProperty("live");
        Assert.Equal(200m, Dec(drift, "unexplained"));
        Assert.Equal(200m, Dec(Find(drift.GetProperty("elements"), "element", "expenses"), "unexplained"));
        Assert.Equal(200m, Dec(Find(drift.GetProperty("sourceChanges"), "code", "expense_added"), "value"));
        Assert.Equal(1m, Dec(Find(drift.GetProperty("sourceChanges"), "code", "voucher_status_changed"), "value"));
        Assert.Equal((sheets, lates), (await s.Db.ManufacturingCostSheets.CountAsync(), await s.Db.ManufacturingLateCosts.CountAsync()));

        // Recorded through B1, the late cost explains it; the original never moves.
        await LateCostAsync(s.Db, w, id, "expense", "EXP-AFTER-FREEZE", 200m);
        var explained = await OkAsync(s.Db, w.AdminPrincipal, "GET", Reconciliation, "?live=true", Of(id));
        Assert.Equal(0m, Dec(explained, "live", "unexplained"));
        Assert.Equal(Dec(explained, "figures", "original") + 200m, Dec(explained, "live", "live"));

        // A payroll month approved after the freeze moves today's labour; the change is counted, not attributed.
        var run = new PayrollRun { Number = "RUN-B2-OCT", BranchId = TillHarness.Algiers, PeriodYear = 2026, PeriodMonth = 10, Status = PayrollRunStatus.Approved };
        run.Lines.Add(new PayrollRunLine { EmployeeId = w.Karim, Gross = 60_000m, DaysPresent = 20m, AttendanceSource = AttendanceSource.Recorded,
            Elements = [new PayrollRunLineElement { Key = "base", Kind = PayrollElementKind.Base, Qty = 20m, Amount = 60_000m, InSocialBase = true }] });
        s.Db.PayrollRuns.Add(run);
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var payroll = (await OkAsync(s.Db, w.AdminPrincipal, "GET", Reconciliation, "?live=true", Of(id))).GetProperty("live");
        Assert.NotEqual(0m, Dec(Find(payroll.GetProperty("elements"), "element", "labour"), "unexplained"));
        Assert.Equal(1m, Dec(Find(payroll.GetProperty("sourceChanges"), "code", "rate_changed"), "value"));

        // The register compares the page it shows the same way.
        var history = await OkAsync(s.Db, w.AdminPrincipal, "GET", History, "?live=true");
        var row = history.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("orderId").GetGuid() == id);
        Assert.Equal(Dec(payroll, "unexplained"), Dec(row, "live", "unexplained"));
    }

    // ---------- registers ----------

    [Fact]
    public async Task Registers_total_exactly_what_they_list_and_page_deterministically()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        // Three orders need 312 kg of wire; the test world stocks 300 at the line's location.
        (await s.Db.ProductStocks.SingleAsync(x => x.ProductId == w.Wire && x.BranchId == WarehouseA)).StockQty += 300m;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var id = await WorkedAndCompletedAsync(s.Db, w, dayOffset: i);
            await FreezeAsync(s.Db, w, id);
            await LateCostAsync(s.Db, w, id, "expense", $"PV-R{i}", 10m + i);
            await LateCostAsync(s.Db, w, id, "materials", $"MI-R{i}", 1m);
            ids.Add(id);
        }
        var history = await OkAsync(s.Db, w.AdminPrincipal, "GET", History);
        var rows = history.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(rows.Sum(r => Dec(r, "figures", "original")), Dec(history, "totals", "original"));
        Assert.Equal(rows.Sum(r => Dec(r, "figures", "adjustedProvisional")), Dec(history, "totals", "adjustedProvisional"));
        Assert.Equal(36m, Dec(history, "totals", "lateRecognized"));
        Assert.Equal(rows.Select(r => r.GetProperty("orderNumber").GetString()).Order(StringComparer.Ordinal), rows.Select(r => r.GetProperty("orderNumber").GetString()));

        var page1 = await OkAsync(s.Db, w.AdminPrincipal, "GET", History, "?pageSize=2&page=1");
        var page2 = await OkAsync(s.Db, w.AdminPrincipal, "GET", History, "?pageSize=2&page=2");
        Assert.Equal((2, 1, 3), (page1.GetProperty("rows").GetArrayLength(), page2.GetProperty("rows").GetArrayLength(), page2.GetProperty("page").GetProperty("total").GetInt32()));
        Assert.Equal(Dec(history, "totals", "original"), Dec(page2, "totals", "original"));

        var releases = await OkAsync(s.Db, w.AdminPrincipal, "GET", Releases);
        Assert.Equal(Dec(releases, "original"), Dec(releases, "releasedValue"));
        Assert.Equal(36m, Dec(releases, "lateNotAllocated"));
        var late = await OkAsync(s.Db, w.AdminPrincipal, "GET", LateCosts, "?element=materials");
        Assert.Equal((3, 3m), (late.GetProperty("rows").GetArrayLength(), Dec(late, "recognized")));
        var variances = await OkAsync(s.Db, w.AdminPrincipal, "GET", Variances);
        Assert.Equal(0, variances.GetProperty("unavailable").GetInt32());
        var scrap = await OkAsync(s.Db, w.AdminPrincipal, "GET", Scrap);
        Assert.Equal((294m, 6m), (Dec(scrap, "acceptedQty"), Dec(scrap, "rejectedQty")));
        var frozenBasis = await OkAsync(s.Db, w.AdminPrincipal, "GET", History, $"?basis=frozen&from={Today()}&to={Today()}");
        Assert.Equal(3, frozenBasis.GetProperty("page").GetProperty("total").GetInt32());
    }
}
