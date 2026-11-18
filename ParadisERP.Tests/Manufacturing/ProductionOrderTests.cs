using ParadisERP.Application.Contracts;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Catalog;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Tests.Security;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Manufacturing.MfgHarness;
using static ParadisERP.Tests.Manufacturing.ProductionWorld;

namespace ParadisERP.Tests.Manufacturing;

/// <summary>
/// Production orders and their materials (Phase 2, parts A, B and F): the BOM copied at creation, materials
/// reserved at the line, shortages refused unless explicitly overridden, issues, returns, scrap and
/// consumption kept apart and never above the balance, requests retried without a second effect, every
/// line apart from the others, and nothing posted or revalued.
/// </summary>
public class ProductionOrderTests
{
    [Fact]
    public async Task With_stock_operations_switched_off_nothing_operational_is_accepted_but_reads_answer()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        StockOperations = false;
        try
        {
            await RefusedAsync(s.Db, w.AdminPrincipal, 409, "operations_not_enabled", "POST", Orders, w.OrderBody(w.LineA, 100));
            await RefusedAsync(s.Db, w.AdminPrincipal, 409, "operations_not_enabled", "POST", "/api/manufacturing/logs",
                $$"""{"lineId":"{{w.LineA}}","day":"2026-10-20"}""");
            var status = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/operations/status");
            Assert.False(status.GetProperty("stockOperationsEnabled").GetBoolean());
            Assert.Empty((await OkAsync(s.Db, w.AdminPrincipal, "GET", Orders)).EnumerateArray());
        }
        finally { StockOperations = true; }
        Assert.False(await s.Db.ProductionOrders.AnyAsync());
    }

    [Fact]
    public async Task An_order_copies_its_recipe_reserves_its_materials_and_a_retried_request_makes_no_second_order()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var body = w.OrderBody(w.LineA, 200, requestId: "req-order-1");
        var order = await OkAsync(s.Db, w.AdminPrincipal, "POST", Orders, body);

        Assert.Equal("MO-000001", order.GetProperty("order").GetProperty("number").GetString());
        Assert.Equal(2m, order.GetProperty("batches").GetDecimal());
        var wire = order.GetProperty("materials")[0];
        // Two batches: 200 kg of wire net, 10 kg expected waste, 210 kg required — all of it reserved at line A.
        Assert.Equal((200m, 10m, 210m, 210m, 150m),
            (wire.GetProperty("netBaseQty").GetDecimal(), wire.GetProperty("wasteBaseQty").GetDecimal(), wire.GetProperty("requiredBaseQty").GetDecimal(),
             wire.GetProperty("reservedBaseQty").GetDecimal(), wire.GetProperty("estimatedUnitCost").GetDecimal()));
        Assert.Equal(210m * 150m, order.GetProperty("estimatedMaterialCost").GetDecimal());
        Assert.Equal(["Coupe", "Frappe"], order.GetProperty("stages").EnumerateArray().Select(x => x.GetProperty("nameFr").GetString()));
        Assert.Equal(120m, Stage(order, 0).GetProperty("plannedMinutes").GetDecimal());
        Assert.True(Stage(order, 1).GetProperty("isOutput").GetBoolean());

        // The same request again: the same order, not a second one.
        var again = await OkAsync(s.Db, w.AdminPrincipal, "POST", Orders, body);
        Assert.Equal(OrderId(order), OrderId(again));
        Assert.Equal(1, await s.Db.ProductionOrders.CountAsync());

        // A new recipe version approved later leaves the open order exactly as it was made.
        var bom = await s.Db.BomLines.SingleAsync(l => l.BomId == w.Bom);
        bom.Quantity = 150m;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var later = await OkAsync(s.Db, w.AdminPrincipal, "GET", Order, "", Of(OrderId(order)));
        Assert.Equal(210m, later.GetProperty("materials")[0].GetProperty("requiredBaseQty").GetDecimal());
    }

    [Fact]
    public async Task A_shortage_is_refused_and_only_an_explicit_permitted_override_with_a_reason_starts_short()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        await w.CreateOrderAsync(s.Db, w.LineA, 200);   // 210 of the 300 kg reserved

        // A second order needs 210 kg; 90 are free.
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "material_shortage", "POST", Orders, w.OrderBody(w.LineA, 200));
        var preview = await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/preview", w.OrderBody(w.LineA, 200));
        var line = preview.GetProperty("materials")[0];
        Assert.Equal((300m, 210m, 90m, 120m), (line.GetProperty("stockBaseQty").GetDecimal(), line.GetProperty("reservedByOthersBaseQty").GetDecimal(),
            line.GetProperty("freeBaseQty").GetDecimal(), line.GetProperty("shortBaseQty").GetDecimal()));

        await RefusedAsync(s.Db, w.AdminPrincipal, 400, "reason_required", "POST", Orders, w.OrderBody(w.LineA, 200, startShort: true));
        var shortOrder = await OkAsync(s.Db, w.AdminPrincipal, "POST", Orders, w.OrderBody(w.LineA, 200, startShort: true, reason: "Wire arriving Tuesday"));
        Assert.True(shortOrder.GetProperty("order").GetProperty("startedShort").GetBoolean());
        Assert.Equal("Wire arriving Tuesday", shortOrder.GetProperty("shortageReason").GetString());
        Assert.Contains("StartedShort", shortOrder.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task Starting_short_needs_its_own_permission()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        // A supervisor of the line, allowed to create orders but not to start one short.
        var (user, caller) = await ManagerAsync(s.Db, null, PermissionCatalog.ManufacturingView, PermissionCatalog.ManufacturingOrdersCreate);
        s.Db.ProductionLineMembers.Add(new ProductionLineMember { LineId = w.LineA, UserId = user.Id, Role = ProductionLineMemberRole.Supervisor });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        await RefusedAsync(s.Db, caller, 403, "start_short_forbidden", "POST", Orders, w.OrderBody(w.LineA, 400, startShort: true, reason: "urgent"));
        Assert.False(await s.Db.ProductionOrders.AnyAsync());
    }

    [Fact]
    public async Task Issues_returns_scrap_and_consumption_keep_one_balance_that_never_goes_below_zero()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var order = await w.CreateOrderAsync(s.Db, w.LineA, 200);
        var id = OrderId(order);
        var material = MaterialId(order);
        var path = "/api/manufacturing/orders/{id:guid}/movements";

        // Issue 100 kg, sent twice under one request id: moved once.
        var issue = Movement("Issue", material, 100, "req-issue-1");
        await OkAsync(s.Db, w.AdminPrincipal, "POST", path, issue, Of(id));
        var after = await OkAsync(s.Db, w.AdminPrincipal, "POST", path, issue, Of(id));
        Assert.Equal(200m, await StockAsync(s.Db, w.Wire, WarehouseA));
        var m = after.GetProperty("materials")[0];
        Assert.Equal((100m, 110m, 100m), (m.GetProperty("issuedBaseQty").GetDecimal(), m.GetProperty("reservedBaseQty").GetDecimal(), m.GetProperty("atLineBaseQty").GetDecimal()));

        // The stock row and the document say who, what, where and which document; nothing posted.
        var stockRow = await s.Db.StockMovements.AsNoTracking().SingleAsync(x => x.ProductId == w.Wire);
        var doc = await s.Db.MaterialMovements.AsNoTracking().Include(x => x.Lines).SingleAsync();
        Assert.Equal(("mfg-issue", -100m, 200m, WarehouseA, doc.Id, "MI-000001", (Guid?)w.Admin),
            (stockRow.Reason, stockRow.QtyDelta, stockRow.BalanceAfter, stockRow.BranchId, stockRow.DocumentId, stockRow.DocumentNumber, stockRow.UserId));
        Assert.Equal((ManufacturingPostingState.Pending, w.LineA, WarehouseA, "req-issue-1"), (doc.PostingState, doc.LineId, doc.LocationId, doc.ClientRequestId));
        Assert.Equal((100m, 1m, 100m, 150m, (Guid?)stockRow.Id),
            (doc.Lines[0].EnteredQty, doc.Lines[0].Factor, doc.Lines[0].BaseQty, doc.Lines[0].UnitCostSnapshot, doc.Lines[0].StockMovementId));

        // Consumption, scrap and returns come out of the same 100 kg.
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/consumptions", Consume(material, 70, requestId: "req-c-1"), Of(id));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/consumptions", Consume(material, 70, requestId: "req-c-1"), Of(id));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", path, Movement("Scrap", material, 5), Of(id));
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "over_balance", "POST", path, Movement("Return", material, 26), Of(id));
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "over_balance", "POST", "/api/manufacturing/orders/{id:guid}/consumptions", Consume(material, 26), Of(id));
        var returned = await OkAsync(s.Db, w.AdminPrincipal, "POST", path, Movement("Return", material, 25), Of(id));
        m = returned.GetProperty("materials")[0];
        Assert.Equal((100m, 25m, 70m, 5m, 0m),
            (m.GetProperty("issuedBaseQty").GetDecimal(), m.GetProperty("returnedBaseQty").GetDecimal(), m.GetProperty("consumedBaseQty").GetDecimal(),
             m.GetProperty("scrappedBaseQty").GetDecimal(), m.GetProperty("atLineBaseQty").GetDecimal()));
        // The return is back on the line's shelf; scrap and consumption moved no stock.
        Assert.Equal(225m, await StockAsync(s.Db, w.Wire, WarehouseA));
        Assert.Equal(2, await s.Db.StockMovements.CountAsync(x => x.ProductId == w.Wire));
        Assert.Equal(1, await s.Db.MaterialConsumptions.CountAsync());
    }

    [Fact]
    public async Task An_issue_never_takes_more_than_the_line_holds_nor_another_order_s_reservation()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var first = await w.CreateOrderAsync(s.Db, w.LineA, 200);   // reserves 210
        var second = await OkAsync(s.Db, w.AdminPrincipal, "POST", Orders, w.OrderBody(w.LineA, 50));   // 52.5, free 90
        var path = "/api/manufacturing/orders/{id:guid}/movements";

        // 300 on hand, 210 claimed by the first order: the second may take at most 90.
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "reserved_by_other_orders", "POST", path, Movement("Issue", MaterialId(second), 91), Of(OrderId(second)));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", path, Movement("Issue", MaterialId(second), 90), Of(OrderId(second)));
        // Never fictitious stock: 210 left, the first order cannot take 211.
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "insufficient_stock", "POST", path, Movement("Issue", MaterialId(first), 211), Of(OrderId(first)));
        Assert.Equal(210m, await StockAsync(s.Db, w.Wire, WarehouseA));
    }

    [Fact]
    public async Task A_transfer_out_of_a_line_cannot_take_material_reserved_for_its_orders()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        await w.CreateOrderAsync(s.Db, w.LineA, 200);   // 210 of 300 reserved
        var transfers = new StockTransfers(s.Db, new LedgerPoster(s.Db));
        var actor = new TransferActor(w.Admin, "admin", null, true, true);
        var refused = await transfers.DispatchAsync(new DispatchStockTransferRequest(WarehouseA, TillHarness.Algiers, null,
            [new StockTransferLineRequest(w.Wire, 91m)]), actor);
        Assert.False(refused.Ok);
        Assert.Equal(409, refused.Status);
        Assert.Contains("reserved for production orders", refused.Error);
        s.Db.ChangeTracker.Clear();
        var allowed = await transfers.DispatchAsync(new DispatchStockTransferRequest(WarehouseA, TillHarness.Algiers, null,
            [new StockTransferLineRequest(w.Wire, 90m)]), actor);
        Assert.True(allowed.Ok, allowed.Error);
    }

    [Fact]
    public async Task Each_line_is_apart_another_line_s_order_is_not_found_and_its_stock_is_not_touched()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var order = await w.CreateOrderAsync(s.Db, w.LineA, 100);
        var (user, lineB) = await ManagerAsync(s.Db, null, AllGrants);
        s.Db.ProductionLineMembers.Add(new ProductionLineMember { LineId = w.LineB, UserId = user.Id, Role = ProductionLineMemberRole.Manager });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        await RefusedAsync(s.Db, lineB, 404, "not_found", "GET", Order, "", Of(OrderId(order)));
        await RefusedAsync(s.Db, lineB, 404, "not_found", "POST", "/api/manufacturing/orders/{id:guid}/movements",
            Movement("Issue", MaterialId(order), 10), Of(OrderId(order)));
        await RefusedAsync(s.Db, lineB, 404, "not_found", "POST", Orders, w.OrderBody(w.LineA, 10));
        Assert.Empty((await OkAsync(s.Db, lineB, "GET", Orders)).EnumerateArray());

        // Line B's own order draws on line B's wire only.
        var own = await OkAsync(s.Db, lineB, "POST", Orders, w.OrderBody(w.LineB, 40));
        await OkAsync(s.Db, lineB, "POST", "/api/manufacturing/orders/{id:guid}/movements", Movement("Issue", MaterialId(own), 40, "req-b-1"), Of(OrderId(own)));
        Assert.Equal((300m, 10m), (await StockAsync(s.Db, w.Wire, WarehouseA), await StockAsync(s.Db, w.Wire, WarehouseB)));
        // A request id of their own, sent to line A's order, is still not found there: the order is reached first.
        await RefusedAsync(s.Db, lineB, 404, "not_found", "POST", "/api/manufacturing/orders/{id:guid}/movements",
            Movement("Issue", MaterialId(order), 10, "req-b-1"), Of(OrderId(order)));
    }

    [Fact]
    public async Task Holding_stops_movements_and_cancelling_needs_nothing_produced_and_everything_returned()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var order = await w.CreateOrderAsync(s.Db, w.LineA, 100);
        var id = OrderId(order);
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/movements", Movement("Issue", MaterialId(order), 20), Of(id));

        await RefusedAsync(s.Db, w.AdminPrincipal, 400, "reason_required", "POST", "/api/manufacturing/orders/{id:guid}/hold", """{"reason":" "}""", Of(id));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/hold", """{"reason":"Machine down"}""", Of(id));
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "not_open", "POST", "/api/manufacturing/orders/{id:guid}/movements", Movement("Issue", MaterialId(order), 1), Of(id));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/resume", "", Of(id));

        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "materials_at_line", "POST", "/api/manufacturing/orders/{id:guid}/cancel", """{"reason":"Customer cancelled"}""", Of(id));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/movements", Movement("Return", MaterialId(order), 20), Of(id));
        var cancelled = await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/cancel", """{"reason":"Customer cancelled"}""", Of(id));
        Assert.Equal("Cancelled", cancelled.GetProperty("order").GetProperty("status").GetString());
        // A cancelled order claims nothing any more.
        Assert.Equal(0m, cancelled.GetProperty("materials")[0].GetProperty("reservedBaseQty").GetDecimal());
        Assert.Equal(["Created", "Held", "Resumed", "Cancelled"], cancelled.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task A_manager_releases_part_of_a_reservation_with_a_reason_never_below_what_left_the_stock()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var order = await w.CreateOrderAsync(s.Db, w.LineA, 200);   // 210 reserved
        var id = OrderId(order);
        var path = "/api/manufacturing/orders/{id:guid}/materials/{materialId:guid}/release-reservation";
        var route = new[] { Of(id), ("materialId", MaterialId(order).ToString()) };
        await RefusedAsync(s.Db, w.AdminPrincipal, 400, "invalid_quantity", "POST", path, """{"baseQty":211,"reason":"other order"}""", route);
        var released = await OkAsync(s.Db, w.AdminPrincipal, "POST", path, """{"baseQty":60,"reason":"Urgent order first"}""", route);
        Assert.Equal(150m, released.GetProperty("materials")[0].GetProperty("reservedBaseQty").GetDecimal());
        // Another order can now have the 150 free kg.
        await OkAsync(s.Db, w.AdminPrincipal, "POST", Orders, w.OrderBody(w.LineA, 140));
    }

    [Fact]
    public async Task Nothing_in_phase_two_posts_revalues_or_touches_the_reference_cost()
    {
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var entries = await s.Db.LedgerEntries.CountAsync();
        var order = await w.CreateOrderAsync(s.Db, w.LineA, 100);
        var id = OrderId(order);
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/movements", Movement("Issue", MaterialId(order), 105), Of(id));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/expenses",
            """{"description":"Galvanising, outside","amount":12000,"paymentVoucherId":null,"clientRequestId":"req-exp-1"}""", Of(id));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", "/api/manufacturing/orders/{id:guid}/expenses",
            """{"description":"Galvanising, outside","amount":12000,"paymentVoucherId":null,"clientRequestId":"req-exp-1"}""", Of(id));

        Assert.Equal(entries, await s.Db.LedgerEntries.CountAsync());
        Assert.Equal((150m, 400m), ((await s.Db.Products.AsNoTracking().SingleAsync(p => p.Id == w.Wire)).CostPrice,
            (await s.Db.Products.AsNoTracking().SingleAsync(p => p.Id == w.Nails)).CostPrice));
        var inputs = await OkAsync(s.Db, w.AdminPrincipal, "GET", "/api/manufacturing/orders/{id:guid}/cost-inputs", "", Of(id));
        var m = inputs.GetProperty("materials")[0];
        Assert.Equal((105m, 105m * 150m, 105m * 150m), (m.GetProperty("usedBaseQty").GetDecimal(), m.GetProperty("usedCostAtSnapshot").GetDecimal(),
            m.GetProperty("estimatedCost").GetDecimal()));
        Assert.Equal(12000m, inputs.GetProperty("expensesTotal").GetDecimal());
        Assert.Single(inputs.GetProperty("expenses").EnumerateArray());
    }

    [Fact]
    public async Task The_expenses_linked_to_one_voucher_never_exceed_its_amount_and_another_branch_s_voucher_is_not_found()
    {
        // Costing plan F9, step A0.
        await using var s = await NewAsync();
        var w = await SetupAsync(s.Db);
        var algiers = new PaymentVoucher { Number = "PV-000901", BranchId = TillHarness.Algiers, Kind = PaymentVoucherKind.Expense, Amount = 10000m,
            MethodKey = "cash", Status = PaymentVoucherStatus.Paid };
        var oran = new PaymentVoucher { Number = "PV-000902", BranchId = TillHarness.Oran, Kind = PaymentVoucherKind.Expense, Amount = 5000m,
            MethodKey = "cash", Status = PaymentVoucherStatus.Paid };
        var cancelled = new PaymentVoucher { Number = "PV-000903", BranchId = TillHarness.Algiers, Kind = PaymentVoucherKind.Expense, Amount = 5000m,
            MethodKey = "cash", Status = PaymentVoucherStatus.Cancelled };
        var refund = new PaymentVoucher { Number = "PV-000904", BranchId = TillHarness.Algiers, Kind = PaymentVoucherKind.CustomerRefund, Amount = 5000m,
            MethodKey = "cash", Status = PaymentVoucherStatus.Paid };
        s.Db.PaymentVouchers.AddRange(algiers, oran, cancelled, refund);
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        var first = OrderId(await w.CreateOrderAsync(s.Db, w.LineA, 100));
        var second = OrderId(await w.CreateOrderAsync(s.Db, w.LineA, 50));
        const string path = "/api/manufacturing/orders/{id:guid}/expenses";
        static string Expense(decimal amount, Guid? voucher, string request) => System.Text.Json.JsonSerializer.Serialize(
            new { description = "Galvanising, outside", amount, paymentVoucherId = voucher, clientRequestId = request });

        await OkAsync(s.Db, w.AdminPrincipal, "POST", path, Expense(6000m, algiers.Id, "req-v-1"), Of(first));
        // The same voucher on another order takes only what is left of it, to the last centime.
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "voucher_amount_exceeded", "POST", path, Expense(4000.01m, algiers.Id, "req-v-2"), Of(second));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", path, Expense(4000m, algiers.Id, "req-v-3"), Of(second));
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "voucher_amount_exceeded", "POST", path, Expense(1m, algiers.Id, "req-v-4"), Of(first));
        // A retried request is still the one expense, not a second claim on the voucher; an expense with no voucher is not capped.
        await OkAsync(s.Db, w.AdminPrincipal, "POST", path, Expense(6000m, algiers.Id, "req-v-1"), Of(first));
        await OkAsync(s.Db, w.AdminPrincipal, "POST", path, Expense(20000m, null, "req-v-5"), Of(first));

        // A manager held to Algiers, on line A: Oran's voucher is not found; Algiers' is reached, and full.
        var (user, manager) = await ManagerAsync(s.Db, TillHarness.Algiers, "manufacturing.view", "manufacturing.orders.manage");
        s.Db.ProductionLineMembers.Add(new ProductionLineMember { LineId = w.LineA, UserId = user.Id, Role = ProductionLineMemberRole.Manager });
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        await RefusedAsync(s.Db, manager, 404, "not_found", "POST", path, Expense(100m, oran.Id, "req-v-6"), Of(first));
        await RefusedAsync(s.Db, manager, 409, "voucher_amount_exceeded", "POST", path, Expense(100m, algiers.Id, "req-v-7"), Of(first));
        // The Admin reaches every branch's voucher.
        await OkAsync(s.Db, w.AdminPrincipal, "POST", path, Expense(5000m, oran.Id, "req-v-8"), Of(first));
        // Existing cancelled links stay historical, but neither a cancelled nor a non-expense voucher starts a new allocation.
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "voucher_cancelled", "POST", path, Expense(1m, cancelled.Id, "req-v-9"), Of(first));
        await RefusedAsync(s.Db, w.AdminPrincipal, 409, "voucher_not_expense", "POST", path, Expense(1m, refund.Id, "req-v-10"), Of(first));

        var linked = await s.Db.ProductionOrderExpenses.AsNoTracking().Where(e => e.PaymentVoucherId != null)
            .Select(e => new { e.PaymentVoucherId, e.Amount }).ToListAsync();
        Assert.Equal((10000m, 5000m), (linked.Where(e => e.PaymentVoucherId == algiers.Id).Sum(e => e.Amount),
            linked.Where(e => e.PaymentVoucherId == oran.Id).Sum(e => e.Amount)));
    }

    internal static async Task<decimal> StockAsync(ParadisERP.Infrastructure.Persistence.PosDbContext db, Guid product, Guid branch) =>
        (await db.ProductStocks.AsNoTracking().SingleAsync(x => x.ProductId == product && x.BranchId == branch)).StockQty;
}
