using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Rentals;
using AlgeriaPos.Tests.Receivables;
using Microsoft.EntityFrameworkCore;
using static AlgeriaPos.Tests.Rentals.RentalTestPaper;

namespace AlgeriaPos.Tests.Rentals;

public partial class RentalDocumentTests
{
    [Theory]
    [InlineData(10, 0, 35700, 0, "NotUsed")]
    [InlineData(10, 35700, 0, 34510, "Used")]
    [InlineData(10, 0, 34510, 0, "Used")]
    [InlineData(10, 35700, 0, 35700, "NotUsed")]
    [InlineData(4, 30000, 5700, 8580, "NotUsed")]
    public async Task Taxed_unused_return_splits_gross_credit_between_debt_and_refund(
        decimal returnedQty, decimal paidBeforeReturn, decimal expectedDebtRelief, decimal expectedRefund, string usage)
    {
        await using var h = await ReceivableHarness.NewAsync(rentalTaxRate: 19);
        var configured = await h.Rentals.UpdateSettingsAsync(new UpdateRentalSettingsRequest(1.5m, false, MinimumDays: 0, TaxRate: 19m));
        Assert.True(configured.Ok, configured.Message);
        var created = await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            Contractor, Algiers, [new RentalContractLineRequest(ReceivableHarness.Scaffold, 10, 30, 100m, 2000m)])), "admin");
        Assert.True(created.Ok, created.Message);
        var contract = created.Value!;
        var lineId = contract.Lines.Single().Id;
        Assert.Equal(19m, contract.Lines.Single().TaxRate);
        Assert.Equal(100m, contract.Lines.Single().RentUnitPrice);
        var issuedAt = Utc("2026-10-13T08:00:00Z");
        var returnedAt = Utc("2026-10-13T14:00:00Z");
        Assert.Equal(AlgeriaPos.Application.Rentals.RentalPricing.LocalDay(issuedAt, h.Rentals.CompanyZone()),
            AlgeriaPos.Application.Rentals.RentalPricing.LocalDay(returnedAt, h.Rentals.CompanyZone()));
        Assert.True((await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 10)], issuedAt)), null, "issuer")).Ok);
        Assert.Equal(35700m, await h.BalanceAsync());
        if (paidBeforeReturn > 0)
        {
            var receipt = await h.Receivables.CreateReceiptAsync(new CreateReceiptRequest(
                Contractor, Algiers, paidBeforeReturn, "cash", ContractId: contract.Id), "cashier");
            Assert.True(receipt.Ok, receipt.Message);
        }
        var request = Driven(new ReturnNoteRequest(Algiers,
            [new ReturnNoteLineRequest(lineId, QtyOk: returnedQty, SameDayUsage: usage)], returnedAt, CountReturnDay: false));

        var preview = await h.Rentals.PreviewReturnAsync(contract.Id, request);
        Assert.True(preview!.CanConfirm, string.Join(",", preview.Problems));
        Assert.Equal(paidBeforeReturn, preview.PreviouslyPaid);
        Assert.Equal(expectedDebtRelief, preview.ReliefDebtReduction);
        Assert.Equal(expectedRefund, preview.ReliefPaymentVoucher);

        var result = await h.Rentals.ReturnAsync(contract.Id, request, null, "clerk");
        Assert.True(result.Ok, result.Message);
        var returned = await h.Db.ReturnNotes.SingleAsync();
        var unusedPerUnit = usage == "Used" ? 2900m : 3000m;
        Assert.Equal(returnedQty * unusedPerUnit, returned.UnusedRent);
        Assert.Equal(expectedDebtRelief, returned.ReliefDebtReduction);
        Assert.Equal(expectedRefund, returned.ReliefPaymentVoucher);
        Assert.Equal(preview.CustomerCredit, Math.Max(0m, -(await h.Db.Customers.SingleAsync(c => c.Id == Contractor)).Balance));
        Assert.Equal(35700m - paidBeforeReturn - returnedQty * unusedPerUnit * 1.19m,
            (await h.Db.Customers.SingleAsync(c => c.Id == Contractor)).Balance);
    }

    [Fact]
    public async Task Same_day_return_requires_usage_and_does_not_move_stock_on_rejection()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 10, days: 30), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        Assert.True((await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 10)], Utc("2026-10-13T08:00:00Z"))), null, "issuer")).Ok);

        var missing = await h.Rentals.ReturnAsync(contract.Id,
            Driven(new ReturnNoteRequest(Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 10)], Utc("2026-10-13T14:00:00Z"))), null, "clerk");

        Assert.Equal(RentalFailure.Invalid, missing.Failure);
        Assert.Empty(await h.Db.ReturnNotes.ToListAsync());
        Assert.Equal(10m, (await h.StockAsync(Scaffold, Algiers))!.OnRentQty);
    }

    [Theory]
    [InlineData("Used", 1000)]
    [InlineData("NotUsed", 0)]
    public async Task Same_day_usage_prices_exactly_one_day_or_zero_and_returns_physical_stock(string usage, decimal charge)
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 10, days: 30), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        Assert.True((await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 10)], Utc("2026-10-13T08:00:00Z"))), null, "issuer")).Ok);

        var result = await h.Rentals.ReturnAsync(contract.Id,
            Driven(new ReturnNoteRequest(Algiers,
                [new ReturnNoteLineRequest(lineId, QtyOk: 10, SameDayUsage: usage)], Utc("2026-10-13T14:00:00Z"))), null, "clerk");

        Assert.True(result.Ok, result.Message);
        Assert.Equal(charge, result.Value!.Charges.Where(c => c.Kind == AlgeriaPos.Domain.RentalChargeKind.Rent).Sum(c => c.Amount));
        Assert.Equal(0m, (await h.StockAsync(Scaffold, Algiers))!.OnRentQty);
        var allocation = await h.Db.ReturnNoteLineAllocations.SingleAsync();
        Assert.Equal(Enum.Parse<SameDayUsage>(usage), allocation.SameDayUsage);
        Assert.Equal(usage == "Used" ? 1 : 0, allocation.ChargeableDays);
        Assert.Equal(charge, allocation.ActualRentNet);
    }

    [Fact]
    public async Task Same_day_missing_goods_keep_existing_rental_and_compensation_rules_without_usage_choice()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 10, days: 30), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        Assert.True((await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 10)], Utc("2026-10-13T08:00:00Z"))), null, "issuer")).Ok);

        var result = await h.Rentals.ReturnAsync(contract.Id,
            Driven(new ReturnNoteRequest(Algiers,
                [new ReturnNoteLineRequest(lineId, QtyMissing: 10)], Utc("2026-10-13T14:00:00Z"))), null, "clerk");

        Assert.True(result.Ok, result.Message);
        Assert.Equal(1000m, result.Value!.Charges.Where(c => c.Kind == AlgeriaPos.Domain.RentalChargeKind.Rent).Sum(c => c.Amount));
        Assert.Contains(result.Value.Charges, c => c.Kind == AlgeriaPos.Domain.RentalChargeKind.Missing);
        var line = await h.Db.ReturnNoteLines.SingleAsync();
        Assert.Equal(SameDayUsage.NotApplicable, line.SameDayUsage);
        Assert.Equal(RentalReturnCondition.Missing,
            (await h.Db.ReturnNoteLineAllocations.SingleAsync()).Condition);
        Assert.Equal(0m, (await h.StockAsync(Scaffold, Algiers))!.OnRentQty);
    }

    [Fact]
    public async Task One_return_can_split_one_contract_line_into_used_and_unused_quantities()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 100, days: 30), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        Assert.True((await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 100)], Utc("2026-10-13T08:00:00Z"))), null, "issuer")).Ok);

        var result = await h.Rentals.ReturnAsync(contract.Id,
            Driven(new ReturnNoteRequest(Algiers,
            [
                new ReturnNoteLineRequest(lineId, QtyOk: 20, SameDayUsage: "Used"),
                new ReturnNoteLineRequest(lineId, QtyOk: 30, SameDayUsage: "NotUsed")
            ], Utc("2026-10-13T14:00:00Z"))), null, "clerk");

        Assert.True(result.Ok, result.Message);
        Assert.Equal(2000m, result.Value!.Charges.Where(c => c.Kind == AlgeriaPos.Domain.RentalChargeKind.Rent).Sum(c => c.Amount));
        Assert.Equal(50m, (await h.Rentals.TotalsAsync([contract.Id]))[contract.Id].StillOutQty);
        Assert.Equal(50m, (await h.StockAsync(Scaffold, Algiers))!.OnRentQty);
        var allocations = (await h.Db.ReturnNoteLineAllocations.ToListAsync()).OrderBy(a => a.Qty).ToList();
        Assert.Equal(new[] { (20m, SameDayUsage.Used, 1), (30m, SameDayUsage.NotUsed, 0) },
            allocations.Select(a => (a.Qty, a.SameDayUsage, a.ChargeableDays)));
    }

    [Fact]
    public async Task Same_company_calendar_date_is_used_instead_of_utc_date()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 1, days: 30), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        var issuedAt = Utc("2026-10-13T22:30:00Z");
        var returnedAt = Utc("2026-10-13T23:15:00Z");
        Assert.Equal(TimeSpan.FromHours(1), h.Rentals.CompanyZone().GetUtcOffset(issuedAt));
        Assert.Equal(new DateOnly(2026, 10, 13), AlgeriaPos.Application.Rentals.RentalPricing.LocalDay(issuedAt, h.Rentals.CompanyZone()));
        Assert.Equal(new DateOnly(2026, 10, 14), AlgeriaPos.Application.Rentals.RentalPricing.LocalDay(returnedAt, h.Rentals.CompanyZone()));
        Assert.True((await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 1)], issuedAt)), null, "issuer")).Ok);

        var result = await h.Rentals.ReturnAsync(contract.Id,
            Driven(new ReturnNoteRequest(Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 1)], returnedAt)), null, "clerk");

        Assert.True(result.Ok, result.Message);
        Assert.Equal(SameDayUsage.NotApplicable, (await h.Db.ReturnNoteLines.SingleAsync()).SameDayUsage);
        Assert.NotEqual(0, result.Value!.Charges.Where(c => c.Kind == AlgeriaPos.Domain.RentalChargeKind.Rent).Sum(c => c.Amount));
    }
    [Fact]
    public async Task Different_items_keep_their_own_return_allocations_and_saved_audit()
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 10, days: 30) with {
            Lines = [new RentalContractLineRequest(Scaffold, 10, 30, 100m, 2000m),
                     new RentalContractLineRequest(Coupler, 10, 30, 20m, 100m)] }, "admin")).Value!;
        var first = contract.Lines.Single(l => l.ProductId == Scaffold);
        var second = contract.Lines.Single(l => l.ProductId == Coupler);
        Assert.True((await h.Rentals.IssueAsync(contract.Id, Driven(new IssueNoteRequest(Algiers,
            [new IssueNoteLineRequest(first.Id, 10), new IssueNoteLineRequest(second.Id, 10)],
            Utc("2026-10-13T08:00:00Z"))), null, "issuer")).Ok);
        var result = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(Algiers,
            [new ReturnNoteLineRequest(first.Id, QtyOk: 10, SameDayUsage: "NotUsed"),
             new ReturnNoteLineRequest(second.Id, QtyOk: 10, SameDayUsage: "Used")],
            Utc("2026-10-13T14:00:00Z"))), null, "clerk");
        Assert.True(result.Ok, result.Message);
        h.Db.ChangeTracker.Clear();
        var saved = (await h.Rentals.ReadAsync(contract.Id))!.ReturnNotes.Single();
        Assert.All(saved.Lines, l => Assert.Single(l.Allocations));
        Assert.Equal(200m, saved.Lines.Single(l => l.ContractLineId == second.Id).Allocations.Single().ActualRentNet);
        Assert.Equal(35800m, saved.UnusedRent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Late_preview_keeps_the_decision_visible_and_matches_saved_charges(bool chargeLate)
    {
        await using var h = await NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 1, days: 3), "admin")).Value!;
        var line = contract.Lines.Single();
        Assert.True((await h.Rentals.IssueAsync(contract.Id, Driven(new IssueNoteRequest(Algiers,
            [new IssueNoteLineRequest(line.Id, 1)], Utc("2026-10-01T08:00:00Z"))), null, "issuer")).Ok);
        var request = Driven(new ReturnNoteRequest(Algiers, [new ReturnNoteLineRequest(line.Id, QtyOk: 1)],
            Utc("2026-10-06T08:00:00Z"), ChargeOverdue: chargeLate, LateWaiverReason: "Approved exception"));
        var preview = await h.Rentals.PreviewReturnAsync(contract.Id, request);
        Assert.NotNull(preview!.Late);
        Assert.Equal(2, preview.Late.Days);
        var saved = await h.Rentals.ReturnAsync(contract.Id, request, null, "clerk");
        Assert.True(saved.Ok, saved.Message);
        Assert.Equal(preview.ChargeTotal, saved.Value!.Charges.Sum(c => c.Amount));
        Assert.Equal(preview.ChargeTotal, (await h.Db.ReturnNoteLineAllocations.SingleAsync()).ActualRentNet);
    }

}
