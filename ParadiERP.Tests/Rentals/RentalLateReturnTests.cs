using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ParadiERP.Application.Contracts;
using ParadiERP.Application.Regional;
using ParadiERP.Application.Rentals;
using ParadiERP.Infrastructure.Rentals;
using Microsoft.EntityFrameworkCore;
using static ParadiERP.Tests.Rentals.RentalTestPaper;
using ChargeKind = ParadiERP.Application.Rentals.RentalChargeKind;
using DomainKind = ParadiERP.Domain.RentalChargeKind;
using LedgerAccounts = ParadiERP.Domain.LedgerAccounts;
using LedgerSources = ParadiERP.Domain.LedgerSources;

namespace ParadiERP.Tests.Rentals;

/// <summary>
/// Goods back after their period, and the question it asks (the owner, 2026-10-08: "if the client
/// returns the rented items after the time they were due, the system must ask whether to take fees
/// for a day or not"). The arithmetic alone: waiving drops the late days and nothing else.
/// </summary>
public class RentalLateReturnTests
{
    private static DateOnly Day(string iso) => DateOnly.Parse(iso, CultureInfo.InvariantCulture);

    private static ContractSettlement BackOnThe5th(bool waived)
    {
        // Ten units for three days from the 1st, so due on the 4th; back on the 5th — one late day.
        var line = new RentalLine(Guid.NewGuid(), 10, 3, RentUnitPrice: 100, DamageUnitPrice: 2000, OverdueUnitPrice: 150);
        return RentalSettlement.Settle(
            new RentalContractInput(
                [line],
                [new IssueBatch(Guid.NewGuid(), line.Id, Day("2026-03-01"), 10)],
                [new ReturnEntry(Guid.NewGuid(), line.Id, Day("2026-03-05"), QtyOk: 10, WaivesOverdue: waived)],
                []),
            new RegionalSettings { CurrencyDecimals = 2 });
    }

    [Fact]
    public void Charged_the_late_day_is_overdue_at_the_price_signed_on_the_line()
    {
        var settlement = BackOnThe5th(waived: false);

        var late = settlement.Charges.Single(c => c.Kind == ChargeKind.Overdue);
        Assert.Equal((1, 150m, 1_500m), (late.Days, late.UnitPrice, late.Amount));
        Assert.Equal(3_000m, settlement.Total(ChargeKind.Rent));
        Assert.True(RentalLateReturn.IsLate(settlement.Charges));
    }

    [Fact]
    public void Waived_the_late_day_is_not_charged_and_the_agreed_period_is_untouched()
    {
        var settlement = BackOnThe5th(waived: true);

        Assert.DoesNotContain(settlement.Charges, c => c.Kind == ChargeKind.Overdue);
        // The three agreed days are still the three agreed days, and none of them is given back.
        Assert.Equal(3_000m, settlement.Total(ChargeKind.Rent));
        Assert.Equal(0m, settlement.UnusedRent);
        Assert.True(settlement.IsClosed);
    }

    [Fact]
    public void Waiving_is_written_down_and_charging_needs_nothing()
    {
        Assert.Equal("lateWaiverNeedsReason", RentalLateReturn.WaiverProblem(false, null));
        Assert.Equal("lateWaiverNeedsReason", RentalLateReturn.WaiverProblem(false, "  "));
        Assert.Null(RentalLateReturn.WaiverProblem(false, "Client fidèle"));
        Assert.Null(RentalLateReturn.WaiverProblem(true, null));
        Assert.Null(RentalLateReturn.WaiverProblem(null, null));
    }

    private static readonly RegionalSettings TwoDecimals = new() { CurrencyDecimals = 2 };

    /// <summary>
    /// Ten units for three days from the 1st at 100 a day, 150 a late day; back on the 8th — four
    /// late days, 6,000 of lateness. The answer is yes, less the deduction given.
    /// </summary>
    private static ContractSettlement BackOnThe8th(int? daysWaived = null, decimal? amountWaived = null)
    {
        var line = new RentalLine(Guid.NewGuid(), 10, 3, RentUnitPrice: 100, DamageUnitPrice: 2000, OverdueUnitPrice: 150);
        return RentalSettlement.Settle(
            new RentalContractInput(
                [line],
                [new IssueBatch(Guid.NewGuid(), line.Id, Day("2026-03-01"), 10)],
                [new ReturnEntry(Guid.NewGuid(), line.Id, Day("2026-03-08"), QtyOk: 10,
                    LateDaysWaived: daysWaived, LateAmountWaived: amountWaived,
                    LateAmountFull: amountWaived is null ? null : 6_000m)],
                []),
            TwoDecimals);
    }

    [Fact]
    public void A_deduction_in_days_takes_that_many_late_days_off_and_charges_the_rest_at_the_signed_price()
    {
        var settlement = BackOnThe8th(daysWaived: 1);

        var late = settlement.Charges.Single(c => c.Kind == ChargeKind.Overdue);
        Assert.Equal((3, 150m, 4_500m), (late.Days, late.UnitPrice, late.Amount));
        Assert.Equal(3_000m, settlement.Total(ChargeKind.Rent));
        Assert.True(settlement.IsClosed);
    }

    [Fact]
    public void A_deduction_of_every_late_day_charges_none_of_them()
    {
        var settlement = BackOnThe8th(daysWaived: 4);

        Assert.DoesNotContain(settlement.Charges, c => c.Kind == ChargeKind.Overdue);
        Assert.Equal(3_000m, settlement.Total(ChargeKind.Rent));
    }

    [Fact]
    public void A_deduction_as_an_amount_charges_every_late_day_at_the_same_share_of_its_price()
    {
        var settlement = BackOnThe8th(amountWaived: 1_000m);

        // 6,000 less 1,000: four days still, each at 125 rather than 150.
        var late = settlement.Charges.Single(c => c.Kind == ChargeKind.Overdue);
        Assert.Equal((4, 125m, 5_000m), (late.Days, late.UnitPrice, late.Amount));
        Assert.Equal(3_000m, settlement.Total(ChargeKind.Rent));
    }

    [Fact]
    public void An_amount_spread_over_lines_charges_exactly_what_was_decided_whatever_the_rounding()
    {
        // Three lines, one unit each, one late day at 100 each: 300 of lateness, 100 taken off.
        // Two thirds of 100 is 66.67 rounded three times — 200.01 — so a centime comes off one of them.
        var lines = Enumerable.Range(0, 3)
            .Select(_ => new RentalLine(Guid.NewGuid(), 1, 3, RentUnitPrice: 100, DamageUnitPrice: 2000, OverdueUnitPrice: 100))
            .ToList();
        var noteId = Guid.NewGuid();
        var settlement = RentalSettlement.Settle(
            new RentalContractInput(
                lines,
                lines.Select(l => new IssueBatch(Guid.NewGuid(), l.Id, Day("2026-03-01"), 1)).ToList(),
                lines.Select(l => new ReturnEntry(noteId, l.Id, Day("2026-03-05"), QtyOk: 1,
                    LateAmountWaived: 100m, LateAmountFull: 300m)).ToList(),
                []),
            TwoDecimals);

        var late = settlement.Charges.Where(c => c.Kind == ChargeKind.Overdue).Select(c => c.Amount).Order().ToList();
        Assert.Equal([66.66m, 66.67m, 66.67m], late);
        Assert.Equal(200m, settlement.Total(ChargeKind.Overdue));
    }

    [Fact]
    public void A_deduction_is_at_least_a_day_or_a_unit_of_money_and_never_more_than_the_lateness()
    {
        string? Problem(bool? charge, int? days, decimal? amount) =>
            RentalLateReturn.DeductionProblem(charge, days, amount, lateDays: 4, lateAmount: 6_000m, TwoDecimals);

        Assert.Null(Problem(true, null, null));
        Assert.Null(Problem(true, 1, null));
        Assert.Null(Problem(true, 4, null));
        Assert.Equal("lateDeductionDaysInvalid", Problem(true, 0, null));
        Assert.Equal("lateDeductionDaysInvalid", Problem(true, 5, null));

        Assert.Null(Problem(true, null, 0.01m));
        Assert.Null(Problem(true, null, 6_000m));
        Assert.Equal("lateDeductionAmountInvalid", Problem(true, null, 0m));
        Assert.Equal("lateDeductionAmountInvalid", Problem(true, null, 6_000.01m));
        Assert.Equal("lateDeductionAmountInvalid", Problem(true, null, 10.005m));

        // One kind or the other, and only with a yes.
        Assert.Equal("lateDeductionInvalid", Problem(true, 1, 100m));
        Assert.Equal("lateDeductionInvalid", Problem(false, 1, null));
        Assert.Equal("lateDeductionInvalid", Problem(null, null, 100m));
    }

    [Fact]
    public void A_deduction_is_written_down_like_a_waiver()
    {
        Assert.Equal("lateWaiverNeedsReason", RentalLateReturn.WaiverProblem(true, null, deducts: true));
        Assert.Equal("lateWaiverNeedsReason", RentalLateReturn.WaiverProblem(true, " ", deducts: true));
        Assert.Null(RentalLateReturn.WaiverProblem(true, "Client fidèle", deducts: true));
    }
}

/// <summary>The same question on real documents: what is stored, what is posted, what is refused.</summary>
public partial class RentalDocumentTests
{
    /// <summary>
    /// The owner's example again — noon cutoff, one unit for three days at 100 a day, out on the
    /// 26th at 15:00 in Algiers, so its period is the 27th to the 29th and it is due back on the
    /// 30th before noon. <paramref name="multiplier"/> sets the line's overdue price (answer 7:
    /// a late day at the rental price is 1×).
    /// </summary>
    private static async Task<(Harness H, Guid ContractId, Guid LineId)> DueOnThe30thAsync(
        decimal multiplier = 1.5m, decimal taxRate = 0)
    {
        var h = await NewAsync();
        var settings = await h.Rentals.UpdateSettingsAsync(
            new UpdateRentalSettingsRequest(multiplier, false, taxRate, DayCutoffHour: 12));
        Assert.True(settings.Ok, settings.Message);

        var contract = (await h.Rentals.CreateContractAsync(Contract(qty: 1, days: 3), "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        var issued = await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 1)], Utc("2026-09-26T14:00:00"))), null, "storeman");
        Assert.True(issued.Ok, issued.Message);
        return (h, contract.Id, lineId);
    }

    private static ReturnNoteRequest Late(Guid lineId, string utc, bool? charge = null, string? reason = null) =>
        Driven(new ReturnNoteRequest(Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 1)], Utc(utc))
        {
            ChargeOverdue = charge, LateWaiverReason = reason
        });

    [Fact]
    public async Task Back_on_time_asks_nothing_and_an_answer_sent_anyway_is_not_kept()
    {
        var (h, contractId, lineId) = await DueOnThe30thAsync();
        await using var _h = h;

        // 08:00 UTC is 09:00 in Algiers: the 30th before noon is the three days agreed, not one more.
        var back = await h.Rentals.ReturnAsync(contractId, Late(lineId, "2026-09-30T08:00:00", charge: false), null, "clerk");
        Assert.True(back.Ok, back.Message);

        Assert.Equal(300m, back.Value!.Charges.Single(c => c.Kind == DomainKind.Rent).Amount);
        Assert.DoesNotContain(back.Value.Charges, c => c.Kind == DomainKind.Overdue);
        var note = await h.Db.ReturnNotes.AsNoTracking().SingleAsync();
        Assert.False(note.WaiveOverdue);
        Assert.Null(note.LateWaiverReason);
        Assert.Null(note.LateDecidedBy);
    }

    [Fact]
    public async Task Back_late_with_no_answer_is_charged_as_it_always_was()
    {
        var (h, contractId, lineId) = await DueOnThe30thAsync();
        await using var _h = h;

        // The 2nd at 09:00: the 30th and the 1st are late, the return day is not counted.
        var back = await h.Rentals.ReturnAsync(contractId, Late(lineId, "2026-10-02T08:00:00"), null, "clerk");
        Assert.True(back.Ok, back.Message);

        var late = back.Value!.Charges.Single(c => c.Kind == DomainKind.Overdue);
        Assert.Equal((2, 150m, 300m), (late.Days, late.UnitPrice, late.Amount));
        var note = await h.Db.ReturnNotes.AsNoTracking().SingleAsync();
        Assert.False(note.WaiveOverdue);
        // Nobody was asked, so nobody is named.
        Assert.Null(note.LateDecidedBy);
    }

    [Fact]
    public async Task Back_late_and_charged_adds_the_late_day_at_the_line_s_daily_price_with_its_tax()
    {
        // 1×: a late day costs what a rented day costs (answer 7), and hire is taxed at 19 %.
        var (h, contractId, lineId) = await DueOnThe30thAsync(multiplier: 1m, taxRate: 19m);
        await using var _h = h;

        // 14:00 UTC is 15:00 in Algiers, three hours after the noon due time: the 30th is counted.
        var back = await h.Rentals.ReturnAsync(contractId, Late(lineId, "2026-09-30T14:00:00", charge: true), null, "Mourad");
        Assert.True(back.Ok, back.Message);

        var late = back.Value!.Charges.Single(c => c.Kind == DomainKind.Overdue);
        Assert.Equal((1, 100m, 100m, true), (late.Days, late.UnitPrice, late.Amount, late.IsPosted));

        // Posted the way every late day is: income on 708, its tax on 4457, the whole on 411.
        var posted = await h.PostedAsync(LedgerSources.RentalReturn);
        Assert.Equal(-100m, posted[LedgerAccounts.LateIncome]);
        Assert.Equal(-19m, posted[LedgerAccounts.TaxCollected]);
        Assert.Equal(119m, posted[LedgerAccounts.Customers]);
        // The three days invoiced at issue (300 + 57 tax) and the late one on top.
        Assert.Equal(476m, (await h.Db.Customers.AsNoTracking().FirstAsync(c => c.Id == Contractor)).Balance);

        var note = await h.Db.ReturnNotes.AsNoTracking().SingleAsync();
        Assert.False(note.WaiveOverdue);
        Assert.Equal("Mourad", note.LateDecidedBy);
    }

    [Fact]
    public async Task Back_late_and_waived_charges_nothing_for_it_and_only_in_writing()
    {
        var (h, contractId, lineId) = await DueOnThe30thAsync(multiplier: 1m, taxRate: 19m);
        await using var _h = h;

        var unexplained = await h.Rentals.ReturnAsync(contractId, Late(lineId, "2026-09-30T14:00:00", charge: false), null, "Mourad");
        Assert.Equal(RentalFailure.Invalid, unexplained.Failure);
        Assert.Equal(
            "These goods are back after the end of their period. Not charging the late days is the branch manager's decision: write the reason for it.",
            unexplained.Message);
        Assert.Empty(await h.Db.ReturnNotes.AsNoTracking().ToListAsync());

        var waived = await h.Rentals.ReturnAsync(
            contractId, Late(lineId, "2026-09-30T14:00:00", charge: false, reason: " Camion bloqué au barrage "), null, "Mourad");
        Assert.True(waived.Ok, waived.Message);
        Assert.DoesNotContain(waived.Value!.Charges, c => c.Kind == DomainKind.Overdue);
        Assert.Equal(300m, waived.Value.Charges.Single(c => c.Kind == DomainKind.Rent).Amount);

        // Nothing late reaches the books, and the customer owes what the issue invoiced.
        Assert.False(await h.AnyLineOnAsync(LedgerAccounts.LateIncome));
        Assert.Equal(357m, (await h.Db.Customers.AsNoTracking().FirstAsync(c => c.Id == Contractor)).Balance);

        var note = await h.Db.ReturnNotes.AsNoTracking().SingleAsync();
        Assert.True(note.WaiveOverdue);
        Assert.Equal("Camion bloqué au barrage", note.LateWaiverReason);
        Assert.Equal("Mourad", note.LateDecidedBy);

        // Stored, not re-decided: the contract read back later still owes no late day.
        Assert.Equal(0m, (await h.Rentals.SettleAsync(contractId))!.Total(ChargeKind.Overdue));
    }

    [Fact]
    public async Task The_server_prices_the_late_days_itself_whatever_the_request_carries()
    {
        var (h, contractId, lineId) = await DueOnThe30thAsync();
        await using var _h = h;

        // As the wire delivers it (web defaults, as Program.cs configures them), with figures a
        // tampered or stale screen might add. None of them is a field the server reads.
        var json = $$"""
            {
              "branchId": "{{Algiers}}", "returnedAtUtc": "2026-10-02T08:00:00Z",
              "driverName": "Karim", "driverPhone": "0550 12 34 56", "truckNumber": "01234-116-16",
              "chargeOverdue": true, "overdueAmount": 1, "chargeTotal": 1, "lateDays": 0,
              "charges": [{ "kind": "Overdue", "amount": 1, "days": 0, "unitPrice": 0 }],
              "lines": [{ "contractLineId": "{{lineId}}", "qtyOk": 1, "amount": 1, "unitPrice": 0 }]
            }
            """;
        var request = JsonSerializer.Deserialize<ReturnNoteRequest>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web) { ReferenceHandler = ReferenceHandler.IgnoreCycles })!;

        var back = await h.Rentals.ReturnAsync(contractId, request, null, "clerk");
        Assert.True(back.Ok, back.Message);

        // Two late days at the 150 signed on the line, as the engine works it out.
        var late = back.Value!.Charges.Single(c => c.Kind == DomainKind.Overdue);
        Assert.Equal((2, 150m, 300m), (late.Days, late.UnitPrice, late.Amount));
        Assert.Equal(-300m, (await h.PostedAsync(LedgerSources.RentalReturn))[LedgerAccounts.LateIncome]);
    }

    private static ReturnNoteRequest Deducted(Guid lineId, string utc, int? days = null, decimal? amount = null,
        string? reason = "Client fidèle") =>
        Driven(new ReturnNoteRequest(Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 1)], Utc(utc))
        {
            ChargeOverdue = true, LateWaiverReason = reason, LateDaysWaived = days, LateAmountWaived = amount
        });

    // 08:00 UTC on the 3rd is 09:00 in Algiers, before noon: the 30th, the 1st and the 2nd are
    // late — three days at 100, 300 of lateness — and the return day is not.
    private const string ThreeDaysLate = "2026-10-03T08:00:00";

    [Fact]
    public async Task Back_late_less_days_charges_the_rest_and_keeps_who_and_why()
    {
        var (h, contractId, lineId) = await DueOnThe30thAsync(multiplier: 1m, taxRate: 19m);
        await using var _h = h;

        var back = await h.Rentals.ReturnAsync(contractId, Deducted(lineId, ThreeDaysLate, days: 1, reason: " Client fidèle "), null, "Mourad");
        Assert.True(back.Ok, back.Message);

        var late = back.Value!.Charges.Single(c => c.Kind == DomainKind.Overdue);
        Assert.Equal((2, 100m, 200m, true), (late.Days, late.UnitPrice, late.Amount, late.IsPosted));
        var posted = await h.PostedAsync(LedgerSources.RentalReturn);
        Assert.Equal(-200m, posted[LedgerAccounts.LateIncome]);
        Assert.Equal(-38m, posted[LedgerAccounts.TaxCollected]);

        var note = await h.Db.ReturnNotes.AsNoTracking().SingleAsync();
        Assert.False(note.WaiveOverdue);
        Assert.Equal((1, (decimal?)null, (decimal?)null), (note.LateDaysWaived, note.LateAmountWaived, note.LateAmountFull));
        Assert.Equal("Client fidèle", note.LateWaiverReason);
        Assert.Equal("Mourad", note.LateDecidedBy);

        // Stored, not re-decided: read back later, the contract still owes two late days.
        Assert.Equal(200m, (await h.Rentals.SettleAsync(contractId))!.Total(ChargeKind.Overdue));
    }

    [Fact]
    public async Task Back_late_less_an_amount_charges_the_late_total_less_it()
    {
        var (h, contractId, lineId) = await DueOnThe30thAsync(multiplier: 1m, taxRate: 19m);
        await using var _h = h;

        var back = await h.Rentals.ReturnAsync(contractId, Deducted(lineId, ThreeDaysLate, amount: 120m), null, "Mourad");
        Assert.True(back.Ok, back.Message);

        // 300 less 120: the three days still, each at 60.
        var late = back.Value!.Charges.Single(c => c.Kind == DomainKind.Overdue);
        Assert.Equal((3, 60m, 180m), (late.Days, late.UnitPrice, late.Amount));
        Assert.Equal(-180m, (await h.PostedAsync(LedgerSources.RentalReturn))[LedgerAccounts.LateIncome]);
        // The three days invoiced at issue (300 + 57 tax) and 180 + 34.20 of lateness.
        Assert.Equal(571.2m, (await h.Db.Customers.AsNoTracking().FirstAsync(c => c.Id == Contractor)).Balance);

        var note = await h.Db.ReturnNotes.AsNoTracking().SingleAsync();
        Assert.Equal(((int?)null, (decimal?)120m, (decimal?)300m), (note.LateDaysWaived, note.LateAmountWaived, note.LateAmountFull));
        Assert.Equal(180m, (await h.Rentals.SettleAsync(contractId))!.Total(ChargeKind.Overdue));
    }

    [Fact]
    public async Task A_deduction_beyond_the_lateness_or_without_a_reason_is_refused()
    {
        var (h, contractId, lineId) = await DueOnThe30thAsync(multiplier: 1m);
        await using var _h = h;

        foreach (var request in new[]
        {
            Deducted(lineId, ThreeDaysLate, days: 4),
            Deducted(lineId, ThreeDaysLate, amount: 300.01m),
            Deducted(lineId, ThreeDaysLate, days: 1, amount: 50m)
        })
        {
            var refused = await h.Rentals.ReturnAsync(contractId, request, null, "Mourad");
            Assert.Equal(RentalFailure.Invalid, refused.Failure);
            Assert.Equal(
                "A deduction from the late days is in days or as an amount, never both, and never more than the late days or what they come to.",
                refused.Message);
        }

        var unexplained = await h.Rentals.ReturnAsync(contractId, Deducted(lineId, ThreeDaysLate, days: 1, reason: null), null, "Mourad");
        Assert.Equal(RentalFailure.Invalid, unexplained.Failure);
        Assert.Equal(
            "These goods are back after the end of their period. Not charging the late days is the branch manager's decision: write the reason for it.",
            unexplained.Message);
        Assert.Empty(await h.Db.ReturnNotes.AsNoTracking().ToListAsync());

        // The whole lateness is a deduction the branch may make: nothing late is charged.
        var all = await h.Rentals.ReturnAsync(contractId, Deducted(lineId, ThreeDaysLate, amount: 300m), null, "Mourad");
        Assert.True(all.Ok, all.Message);
        Assert.DoesNotContain(all.Value!.Charges, c => c.Kind == DomainKind.Overdue);
    }

    [Fact]
    public async Task The_preview_prices_the_deduction_and_still_shows_the_lateness_before_it()
    {
        var (h, contractId, lineId) = await DueOnThe30thAsync(multiplier: 1m);
        await using var _h = h;

        var preview = await h.Rentals.PreviewReturnAsync(contractId, Deducted(lineId, ThreeDaysLate, amount: 120m));
        Assert.NotNull(preview);
        Assert.True(preview!.CanConfirm, string.Join(", ", preview.Problems));
        Assert.Equal((3, 300m), (preview.Late!.Days, preview.Late.Amount));
        Assert.Equal(180m, preview.Charges.Single(c => c.Kind == "Overdue").Amount);

        var days = await h.Rentals.PreviewReturnAsync(contractId, Deducted(lineId, ThreeDaysLate, days: 2));
        var dayCharge = days!.Charges.Single(c => c.Kind == "Overdue");
        Assert.Equal((1, 100m), (dayCharge.Days, dayCharge.Amount));

        var tooMuch = await h.Rentals.PreviewReturnAsync(contractId, Deducted(lineId, ThreeDaysLate, days: 4));
        Assert.False(tooMuch!.CanConfirm);
        Assert.Contains("lateDeductionDaysInvalid", tooMuch.Problems);

        var unexplained = await h.Rentals.PreviewReturnAsync(contractId, Deducted(lineId, ThreeDaysLate, amount: 120m, reason: null));
        Assert.Contains("lateWaiverNeedsReason", unexplained!.Problems);
    }
}
