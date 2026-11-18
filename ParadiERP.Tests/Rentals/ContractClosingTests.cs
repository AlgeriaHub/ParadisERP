using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Rentals;
using ParadiERP.Tests.Receivables;
using Microsoft.EntityFrameworkCore;
using static ParadiERP.Tests.Rentals.RentalTestPaper;

namespace ParadiERP.Tests.Rentals;

/// <summary>
/// Answers 15 and 22 (owner, 2026-10-02): a contract may be paid at signing, later or in instalments;
/// it closes only when what was paid against it reaches what it is worth, a cheque counts once it is
/// confirmed collected, and the Admin may close it short with the reason written down.
/// </summary>
public class ContractClosingTests
{
    private static readonly Guid Algiers = ReceivableHarness.Algiers;
    private static readonly Guid Contractor = ReceivableHarness.Contractor;

    private static DateTime Day(string iso) => DateTime.Parse(iso + "T09:00:00Z").ToUniversalTime();

    /// <summary>Ten scaffolds for ten days at 1 000 a day, all out on 1 March and all back on the 11th.</summary>
    private static async Task<RentalContract> AllBackAsync(ReceivableHarness h)
    {
        var contract = (await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            Contractor, Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 10, 10, RentUnitPrice: 1_000m, DamageUnitPrice: 2_000m)])),
            "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        Assert.True((await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 10)], Day("2026-03-01"))), null, "m")).Ok);
        var back = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 10)], Day("2026-03-11"))), null, "m");
        Assert.True(back.Ok, back.Message);
        Assert.False(back.Value!.ContractClosed);
        return contract;
    }

    private static Task<RentalContract> StoredAsync(ReceivableHarness h, Guid id) =>
        h.Db.RentalContracts.AsNoTracking().FirstAsync(c => c.Id == id);

    [Fact]
    public async Task Goods_back_and_unpaid_stays_open_and_instalments_close_it_when_they_reach_its_value()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var contract = await AllBackAsync(h);
        var stored = await StoredAsync(h, contract.Id);
        Assert.Equal(RentalContractStatus.Open, stored.Status);
        Assert.NotNull(stored.GoodsBackAtUtc);

        var owed = (await RentalClosure.StandingAsync(h.Db, contract.Id)).Owed;
        Assert.True(owed > 0);

        // Two instalments naming the contract: the first leaves it open, the second closes it.
        CreateReceiptRequest Pay(decimal amount) => new(Contractor, Algiers, amount, "cash", ContractId: contract.Id);
        Assert.True((await h.Receivables.CreateReceiptAsync(Pay(owed - 1_000m), "x")).Ok);
        Assert.Equal(RentalContractStatus.Open, (await StoredAsync(h, contract.Id)).Status);
        Assert.True((await h.Receivables.CreateReceiptAsync(Pay(1_000m), "x")).Ok);

        var closed = await StoredAsync(h, contract.Id);
        Assert.Equal(RentalContractStatus.Closed, closed.Status);
        Assert.Null(closed.ClosedShortReason);
    }

    [Fact]
    public async Task A_cheque_counts_only_once_it_is_confirmed_collected()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var contract = await AllBackAsync(h);
        var owed = (await RentalClosure.StandingAsync(h.Db, contract.Id)).Owed;

        var cheque = await h.Receivables.CreateReceiptAsync(new CreateReceiptRequest(
            Contractor, Algiers, owed, "cheque", InstrumentNumber: "0012345", InstrumentBank: "BNA", ContractId: contract.Id), "x");
        Assert.True(cheque.Ok, cheque.Message);
        Assert.Equal(PaymentStatus.Pending, cheque.Value!.Status);
        Assert.Equal(RentalContractStatus.Open, (await StoredAsync(h, contract.Id)).Status);

        Assert.True((await h.Receivables.SettleReceiptAsync(cheque.Value.Id, "Confirmed", "encaissé", "x")).Ok);
        Assert.Equal(RentalContractStatus.Closed, (await StoredAsync(h, contract.Id)).Status);
    }

    [Fact]
    public async Task The_admin_closes_short_only_with_a_reason_and_only_once_the_goods_are_back()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var contract = await AllBackAsync(h);

        Assert.Equal(RentalFailure.Invalid, (await h.Rentals.CloseShortAsync(contract.Id, "  ", "owner")).Failure);
        var closed = await h.Rentals.CloseShortAsync(contract.Id, "Remise commerciale accordée", "owner");
        Assert.True(closed.Ok, closed.Message);
        var stored = await StoredAsync(h, contract.Id);
        Assert.Equal(RentalContractStatus.Closed, stored.Status);
        Assert.Equal("Remise commerciale accordée", stored.ClosedShortReason);
        Assert.Equal("owner", stored.ClosedShortBy);

        // A contract with goods still out is not finished, whatever is said about it.
        var other = (await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            Contractor, Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 5, 10, RentUnitPrice: 1_000m, DamageUnitPrice: 2_000m)])),
            "admin")).Value!;
        await h.Rentals.IssueAsync(other.Id, Driven(new IssueNoteRequest(
            Algiers, [new IssueNoteLineRequest(other.Lines.Single().Id, 5)], Day("2026-03-01"))), null, "m");
        Assert.Equal(RentalFailure.Invalid, (await h.Rentals.CloseShortAsync(other.Id, "Client parti", "owner")).Failure);
    }

    [Fact]
    public async Task Goods_issued_again_after_all_came_back_keep_the_contract_open_however_much_is_paid()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            Contractor, Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 10, 10, RentUnitPrice: 1_000m, DamageUnitPrice: 2_000m)])),
            "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        await h.Rentals.IssueAsync(contract.Id, Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 5)], Day("2026-03-01"))), null, "m");
        await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 5)], Day("2026-03-05"))), null, "m");
        Assert.NotNull((await StoredAsync(h, contract.Id)).GoodsBackAtUtc);

        // The other five go out: the goods are no longer all back.
        Assert.True((await h.Rentals.IssueAsync(contract.Id, Driven(new IssueNoteRequest(
            Algiers, [new IssueNoteLineRequest(lineId, 5)], Day("2026-03-06"))), null, "m")).Ok);
        Assert.Null((await StoredAsync(h, contract.Id)).GoodsBackAtUtc);

        var owed = (await RentalClosure.StandingAsync(h.Db, contract.Id)).Owed;
        Assert.True((await h.Receivables.CreateReceiptAsync(new CreateReceiptRequest(
            Contractor, Algiers, owed, "cash", ContractId: contract.Id), "x")).Ok);
        Assert.Equal(RentalContractStatus.Open, (await StoredAsync(h, contract.Id)).Status);
    }

    // ---------- answer 8: the extension is a new contract ----------

    /// <summary>Ten scaffolds for ten days at 1 000 a day, out on 1 March: the period ends on the 11th.</summary>
    private static async Task<RentalContract> OutAsync(ReceivableHarness h)
    {
        var contract = (await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            Contractor, Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 10, 10, RentUnitPrice: 1_000m, DamageUnitPrice: 2_000m)])),
            "admin")).Value!;
        Assert.True((await h.Rentals.IssueAsync(contract.Id, Driven(new IssueNoteRequest(
            Algiers, [new IssueNoteLineRequest(contract.Lines.Single().Id, 10)], Day("2026-03-01"))), null, "m")).Ok);
        return contract;
    }

    private Task<ProductStock> StockAsync(ReceivableHarness h) =>
        h.Db.ProductStocks.AsNoTracking().FirstAsync(x => x.ProductId == ReceivableHarness.Scaffold && x.BranchId == Algiers);

    [Theory]
    [InlineData("2026-03-05")]   // asked before the end
    [InlineData("2026-03-15")]   // asked after it: still counted from the end of the first
    public async Task An_extension_carries_the_goods_to_a_new_contract_from_the_day_the_first_period_ends(string askedOn)
    {
        await using var h = await ReceivableHarness.NewAsync();
        var first = await OutAsync(h);
        var onRent = (await StockAsync(h)).OnRentQty;

        var extended = await h.Rentals.ExtendAsNewAsync(first.Id, new ExtendAsNewContractRequest(20, Day(askedOn)), null, "gerant");
        Assert.True(extended.Ok, extended.Message);
        var second = extended.Value!;
        Assert.Equal(first.Id, second.ExtendsContractId);
        Assert.NotEqual(first.Number, second.Number);

        // Nothing moved: the goods never left the site.
        Assert.Equal(onRent, (await StockAsync(h)).OnRentQty);
        Assert.Empty(await h.Db.StockMovements.AsNoTracking().Where(m => m.Reason != "rental-issue").ToListAsync());

        // The first contract's goods are back, charged nothing for overdue, and it closes once paid.
        var stored = await StoredAsync(h, first.Id);
        Assert.NotNull(stored.GoodsBackAtUtc);
        Assert.Empty(await h.Db.RentalCharges.AsNoTracking()
            .Where(c => c.ContractId == first.Id && c.Kind == RentalChargeKind.Overdue).ToListAsync());

        // The new period starts the day the first ended, and is billed as an issue bills it.
        var carried = await h.Db.IssueNotes.AsNoTracking().SingleAsync(n => n.ContractId == second.Id);
        Assert.True(carried.IsCarryOver);
        Assert.Equal(new DateOnly(2026, 3, 11), carried.PeriodStartsOn);
        Assert.True((await RentalClosure.StandingAsync(h.Db, second.Id)).Owed > 0);
        // Booked on that first day, midnight in Algiers (REN-02), however late the extension was asked.
        var billed = await h.Db.LedgerEntries.AsNoTracking()
            .SingleAsync(e => e.SourceType == LedgerSources.RentalIssue && e.SourceId == carried.Id);
        Assert.Equal(new DateTime(2026, 3, 10, 23, 0, 0, DateTimeKind.Utc), billed.EntryDate);

        // One return note on the new contract brings everything home, on time, and frees the stock.
        var line = (await h.Db.RentalContractLines.AsNoTracking().SingleAsync(l => l.ContractId == second.Id));
        Assert.Equal(10m, line.Qty);
        var back = await h.Rentals.ReturnAsync(second.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(line.Id, QtyOk: 10)], Day("2026-03-31"))), null, "m");
        Assert.True(back.Ok, back.Message);
        Assert.DoesNotContain(back.Value!.Charges, c => c.Kind == RentalChargeKind.Overdue);
        Assert.Equal(onRent - 10m, (await StockAsync(h)).OnRentQty);
        Assert.NotNull((await StoredAsync(h, second.Id)).GoodsBackAtUtc);
    }

    [Fact]
    public async Task Nothing_out_is_nothing_to_extend()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var contract = await AllBackAsync(h);
        Assert.Equal(RentalFailure.Invalid,
            (await h.Rentals.ExtendAsNewAsync(contract.Id, new ExtendAsNewContractRequest(20), null, "gerant")).Failure);
        Assert.Equal(RentalFailure.Invalid,
            (await h.Rentals.ExtendAsNewAsync((await OutAsync(h)).Id, new ExtendAsNewContractRequest(0), null, "gerant")).Failure);
    }
}
