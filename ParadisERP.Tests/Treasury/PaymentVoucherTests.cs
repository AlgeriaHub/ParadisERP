using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Receivables;
using ParadisERP.Tests.Receivables;
using Microsoft.EntityFrameworkCore;
using static ParadisERP.Tests.Rentals.RentalTestPaper;

namespace ParadisERP.Tests.Treasury;

/// <summary>
/// Money going out under a payment voucher (the owner's answers 20–27, 2026-09-30).
///
/// <para>
/// Four rules carry these tests: a voucher is numbered on its own series; nothing moves until a
/// manager other than the requester has approved it and somebody has paid it; paying posts a
/// balanced entry and moves the box exactly once; and a refund never pays out more than the
/// return note still owes or more than the customer's account holds in their favour.
/// </para>
/// </summary>
public class PaymentVoucherTests
{
    private static readonly Guid Algiers = ReceivableHarness.Algiers;
    private static readonly Guid Contractor = ReceivableHarness.Contractor;

    private static readonly VoucherActor Owner = new(null, "owner", MayApproveOwn: true);
    private static readonly VoucherActor Manager = new(null, "gerant", MayApproveOwn: false);
    private static readonly VoucherActor Cashier = new(null, "caissier", MayApproveOwn: false);

    private sealed class Algeria : IRegionalSettingsProvider
    {
        public RegionalSettings Get() => new()
        {
            CountryCode = "DZ", CurrencyCode = "DZD", CurrencyDecimals = 2, TimeZone = "Africa/Algiers"
        };
    }

    private static PaymentVouchers Vouchers(ReceivableHarness h) => new(h.Db, new LedgerPoster(h.Db), new Algeria());

    private static DateTime Day(string iso) => DateTime.Parse(iso + "T09:00:00Z").ToUniversalTime();

    private static CreatePaymentVoucherRequest Expense(
        decimal amount = 12_000m, string category = "fuel", string method = "cash",
        string? number = null, string? bank = null) =>
        new(Algiers, PaymentVoucherKind.Expense, amount, method,
            PayeeName: "Station Naftal", ExpenseCategory: category,
            InstrumentNumber: number, InstrumentBank: bank);

    private static async Task<PaymentVoucher> RaiseAsync(PaymentVouchers vouchers, CreatePaymentVoucherRequest r, string by = "caissier")
    {
        var raised = await vouchers.CreateAsync(r, by);
        Assert.True(raised.Ok, raised.Message);
        return raised.Value.Voucher;
    }

    private static async Task<PaymentVoucher> PaidAsync(PaymentVouchers vouchers, CreatePaymentVoucherRequest r)
    {
        var voucher = await RaiseAsync(vouchers, r);
        Assert.True((await vouchers.ApproveAsync(voucher.Id, null, Owner)).Ok);
        var paid = await vouchers.PayAsync(voucher.Id, null, Cashier);
        Assert.True(paid.Ok, paid.Message);
        return paid.Value.Voucher;
    }

    private static async Task<decimal> BoxBalanceAsync(ReceivableHarness h)
    {
        var box = await h.Db.CashBoxes.AsNoTracking().FirstAsync(b => b.BranchId == Algiers && b.IsActive);
        var rows = await h.Db.CashMovements.AsNoTracking().Where(m => m.CashBoxId == box.Id).ToListAsync();
        return rows.Sum(m => m.Direction == CashDirection.In ? m.Amount : -m.Amount);
    }

    /// <summary>
    /// 100 scaffolds out on 1 March for 30 days at 3,000 a month (300,000), paid in cash, and all
    /// of it back on 11 March: 20 days × 100 units × 100 a day = 200,000 given back, and with
    /// nothing owing the whole of it is left as a payment voucher.
    /// </summary>
    private static async Task<(RentalContract Contract, ReturnNote Note)> EarlyReturnAsync(ReceivableHarness h)
    {
        var contract = (await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            Contractor, Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 100, 30, RentUnitPrice: 100m, DamageUnitPrice: 2_000m)])),
            "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        var issued = await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 100)], Day("2026-03-01"))), null, "magasinier");
        Assert.True(issued.Ok, issued.Message);
        Assert.True((await h.ReceiveAsync(300_000m)).Ok);

        var back = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 100)], Day("2026-03-11"))), null, "magasinier");
        Assert.True(back.Ok, back.Message);
        Assert.Equal(200_000m, back.Value!.Note.ReliefPaymentVoucher);
        Assert.Equal(-200_000m, await h.BalanceAsync());
        return (contract, back.Value.Note);
    }

    private static CreatePaymentVoucherRequest Refund(Guid? noteId, decimal amount = 0m, Guid? customerId = null) =>
        new(Algiers, PaymentVoucherKind.CustomerRefund, amount, "cash", CustomerId: customerId, ReturnNoteId: noteId);

    // ---------- answer 20: its own number ----------

    [Fact]
    public async Task Every_voucher_takes_the_next_number_on_its_own_series()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);

        var first = await RaiseAsync(vouchers, Expense());
        var second = await RaiseAsync(vouchers, Expense(category: "transport"));

        Assert.Equal("PV-000001", first.Number);
        Assert.Equal("PV-000002", second.Number);
        // A receipt keeps counting on its own: the two series never share a number.
        Assert.Equal("RCV-000001", (await h.ReceiveAsync(1_000m)).Value!.Number);
    }

    // ---------- answer 25: approval before anything moves ----------

    [Fact]
    public async Task A_pending_voucher_posts_nothing_and_moves_no_cash_and_cannot_be_paid()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);

        var voucher = await RaiseAsync(vouchers, Expense());
        Assert.Equal(PaymentVoucherStatus.Pending, voucher.Status);
        Assert.Equal("caissier", voucher.RequestedBy);

        var early = await vouchers.PayAsync(voucher.Id, null, Cashier);
        Assert.Equal(ReceivableFailure.Conflict, early.Failure);
        Assert.Contains("approv", early.Message);

        Assert.Empty(await h.Db.LedgerEntries.Where(e => e.SourceType == LedgerSources.PaymentVoucher).ToListAsync());
        Assert.Empty(await h.Db.CashMovements.Where(m => m.SourceType == LedgerSources.PaymentVoucher).ToListAsync());
    }

    [Fact]
    public async Task A_manager_does_not_approve_their_own_voucher_but_another_manager_or_the_admin_does()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);

        var own = await RaiseAsync(vouchers, Expense(), by: "gerant");
        var refused = await vouchers.ApproveAsync(own.Id, null, Manager);
        Assert.Equal(ReceivableFailure.Conflict, refused.Failure);
        Assert.Equal(PaymentVoucherStatus.Pending, (await vouchers.ReadAsync(own.Id))!.Status);

        var user = new User { Username = "second", FullName = "Karim Second", Role = UserRole.Manager };
        h.Db.Users.Add(user);
        await h.Db.SaveChangesAsync();
        var approved = await vouchers.ApproveAsync(own.Id, "Plein du camion", new VoucherActor(user.Id, "second", false));
        Assert.True(approved.Ok, approved.Message);
        Assert.Equal(PaymentVoucherStatus.Approved, approved.Value.Voucher.Status);
        // Who and when, as the paper prints it: the person's name, not their login.
        Assert.Equal("Karim Second", approved.Value.Voucher.ApprovedByName);
        Assert.Equal(user.Id, approved.Value.Voucher.ApprovedByUserId);
        Assert.NotNull(approved.Value.Voucher.ApprovedAtUtc);

        // The owner is the administrator and signs his own paper.
        var his = await RaiseAsync(vouchers, Expense(), by: "owner");
        Assert.True((await vouchers.ApproveAsync(his.Id, null, Owner)).Ok);

        // Approving twice is not a second approval.
        Assert.Equal(ReceivableFailure.Conflict, (await vouchers.ApproveAsync(his.Id, null, Owner)).Failure);
    }

    [Fact]
    public async Task Pending_and_approved_vouchers_cancel_with_a_note_and_a_paid_one_never_does()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);

        var pending = await RaiseAsync(vouchers, Expense());
        Assert.Equal(ReceivableFailure.Invalid, (await vouchers.CancelAsync(pending.Id, "  ")).Failure);
        var cancelled = await vouchers.CancelAsync(pending.Id, "Doublon");
        Assert.True(cancelled.Ok, cancelled.Message);
        Assert.Equal(PaymentVoucherStatus.Cancelled, cancelled.Value.Voucher.Status);
        Assert.Equal("Doublon", cancelled.Value.Voucher.StatusNote);
        Assert.Equal(ReceivableFailure.Conflict, (await vouchers.PayAsync(pending.Id, null, Cashier)).Failure);

        var approved = await RaiseAsync(vouchers, Expense());
        await vouchers.ApproveAsync(approved.Id, null, Owner);
        Assert.True((await vouchers.CancelAsync(approved.Id, "Annulé par le gérant")).Ok);

        var paid = await PaidAsync(vouchers, Expense());
        var refused = await vouchers.CancelAsync(paid.Id, "Trop tard");
        Assert.Equal(ReceivableFailure.Conflict, refused.Failure);
        Assert.Contains("A paid voucher is not cancelled", refused.Message);
        Assert.Equal(ReceivableFailure.Conflict, (await vouchers.PayAsync(paid.Id, null, Cashier)).Failure);
    }

    // ---------- answer 24: what an expense posts ----------

    [Theory]
    [InlineData("fuel", LedgerAccounts.FuelExpense)]
    [InlineData("transport", LedgerAccounts.TransportExpense)]
    [InlineData("repair", LedgerAccounts.MaintenanceExpense)]
    [InlineData("purchase", LedgerAccounts.SundryPurchases)]
    [InlineData("other", LedgerAccounts.OtherOperatingExpense)]
    public async Task Paying_an_expense_debits_its_category_and_takes_the_cash_out_of_the_box_once(string category, string account)
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);
        await h.ReceiveAsync(50_000m);
        var before = await BoxBalanceAsync(h);

        var voucher = await RaiseAsync(vouchers, Expense(12_000m, category));
        Assert.Equal(account, voucher.ExpenseAccountCode);
        await vouchers.ApproveAsync(voucher.Id, null, Owner);
        // Approval is a signature, not a payment: the box has not moved yet.
        Assert.Equal(before, await BoxBalanceAsync(h));

        var paid = await vouchers.PayAsync(voucher.Id, null, Cashier);
        Assert.True(paid.Ok, paid.Message);
        Assert.Null(paid.Value.Warning);
        Assert.Equal(PaymentVoucherStatus.Paid, paid.Value.Voucher.Status);
        Assert.Equal("caissier", paid.Value.Voucher.PaidByName);

        var posted = await h.PostedAsync(LedgerSources.PaymentVoucher);
        Assert.Equal(12_000m, posted[account]);
        Assert.Equal(-12_000m, posted[LedgerAccounts.Cash]);
        Assert.Equal(0m, posted.Values.Sum());

        var movement = await h.Db.CashMovements.AsNoTracking().SingleAsync(m => m.SourceType == LedgerSources.PaymentVoucher);
        Assert.Equal(CashMovementKind.Disbursement, movement.Kind);
        Assert.Equal(CashDirection.Out, movement.Direction);
        Assert.Equal(12_000m, movement.Amount);
        Assert.Equal(voucher.Number, movement.Reference);
        Assert.Equal(before - 12_000m, await BoxBalanceAsync(h));
    }

    [Fact]
    public async Task An_expense_names_its_category_and_its_payee()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);

        Assert.Equal(ReceivableFailure.Invalid, (await vouchers.CreateAsync(Expense(category: "lunch"), "x")).Failure);
        Assert.Equal(ReceivableFailure.Invalid, (await vouchers.CreateAsync(
            new CreatePaymentVoucherRequest(Algiers, PaymentVoucherKind.Expense, 100m, "cash", ExpenseCategory: "fuel"), "x")).Failure);
        Assert.Empty(await h.Db.PaymentVouchers.ToListAsync());
    }

    [Fact]
    public async Task A_short_box_warns_and_the_money_still_leaves()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);

        var voucher = await RaiseAsync(vouchers, Expense(90_000m, "repair"));
        await vouchers.ApproveAsync(voucher.Id, null, Owner);
        var paid = await vouchers.PayAsync(voucher.Id, null, Cashier);

        Assert.True(paid.Ok, paid.Message);
        Assert.Equal(PaymentVoucherWarnings.BoxShort, paid.Value.Warning!.Code);
        Assert.Equal("90000", paid.Value.Warning.Args["amount"]);
        Assert.Equal(-90_000m, await BoxBalanceAsync(h));
    }

    // ---------- answer 22: the method, and a cheque's number and bank ----------

    [Fact]
    public async Task A_cheque_needs_its_number_and_bank_and_is_paid_from_the_bank_not_the_box()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);

        Assert.Equal(ReceivableFailure.Invalid, (await vouchers.CreateAsync(Expense(method: "cheque"), "x")).Failure);

        var paid = await PaidAsync(vouchers, Expense(40_000m, "transport", "cheque", "0012345", "BNA Skikda"));
        Assert.Equal("0012345", paid.InstrumentNumber);
        Assert.Equal("BNA Skikda", paid.InstrumentBank);

        var posted = await h.PostedAsync(LedgerSources.PaymentVoucher);
        Assert.Equal(-40_000m, posted[LedgerAccounts.Bank]);
        Assert.False(posted.ContainsKey(LedgerAccounts.Cash));
        Assert.Empty(await h.Db.CashMovements.Where(m => m.SourceType == LedgerSources.PaymentVoucher).ToListAsync());
    }

    [Fact]
    public async Task Credit_is_not_a_way_of_paying_money_out()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var refused = await Vouchers(h).CreateAsync(Expense(method: "credit"), "x");
        Assert.Equal(ReceivableFailure.Invalid, refused.Failure);
    }

    // ---------- answers 24 and 26: a refund over a return note ----------

    [Fact]
    public async Task A_refund_raised_from_a_return_note_carries_the_contract_the_days_and_what_is_still_owed()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);
        var (contract, note) = await EarlyReturnAsync(h);

        var owed = (await vouchers.RefundableAsync(Contractor, null)).Single();
        Assert.Equal(note.Id, owed.ReturnNoteId);
        Assert.Equal(200_000m, owed.StillOwed);
        Assert.Equal(200_000m, owed.Payable);
        Assert.Equal(20, owed.UnusedDays);

        // No amount typed: the voucher is for what the note still owes, and the customer is the
        // contract's.
        var voucher = await RaiseAsync(vouchers, Refund(note.Id));
        Assert.Equal(200_000m, voucher.Amount);
        Assert.Equal(Contractor, voucher.CustomerId);
        Assert.Equal(contract.Id, voucher.ContractId);
        Assert.Equal(20, voucher.UnusedDays);
        Assert.Equal(0m, voucher.DeductedFromDebt);
        Assert.Contains(contract.Number, voucher.Purpose);
        Assert.Contains(note.Number, voucher.Purpose);
        Assert.Contains("20 jours", voucher.Purpose);

        // Raised, so nothing is left to raise on that note.
        Assert.Empty(await vouchers.RefundableAsync(Contractor, null));
        Assert.Equal(0m, (await vouchers.RefundableAsync(null, note.Id)).Single().StillOwed);
    }

    /// <summary>
    /// Found on the browser drive of 2026-09-30: a paid-up customer returned early with one unit
    /// missing, and the return note printed a voucher for the whole unused period ("account
    /// settled, the rest is refunded") while the unit charged on that same note was owed. What the
    /// note charges comes off the refund first (answer 27), so the paper and the voucher agree.
    /// </summary>
    [Fact]
    public async Task What_the_return_note_itself_charges_comes_off_its_refund_before_any_voucher()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);
        var contract = (await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            Contractor, Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 100, 30, RentUnitPrice: 100m, DamageUnitPrice: 2_000m)])),
            "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        Assert.True((await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 100)], Day("2026-03-01"))), null, "magasinier")).Ok);
        Assert.True((await h.ReceiveAsync(300_000m)).Ok);

        // 99 back and 1 lost after 10 of 30 days: 200,000 of unused rent, 2,000 for the lost unit.
        var back = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 99, QtyMissing: 1)], Day("2026-03-11"))), null, "magasinier");
        Assert.True(back.Ok, back.Message);
        Assert.Equal(200_000m, back.Value!.Note.UnusedRent);
        Assert.Equal(2_000m, back.Value.Note.ReliefDebtReduction);
        Assert.Equal(198_000m, back.Value.Note.ReliefPaymentVoucher);
        Assert.Equal(-198_000m, await h.BalanceAsync());

        // The voucher is for exactly what the note says, with nothing left to take off at raising.
        var owed = (await vouchers.RefundableAsync(null, back.Value.Note.Id)).Single();
        Assert.Equal(198_000m, owed.StillOwed);
        Assert.Equal(198_000m, owed.Payable);
        var voucher = await RaiseAsync(vouchers, Refund(back.Value.Note.Id));
        Assert.Equal(198_000m, voucher.Amount);
        Assert.Equal(0m, voucher.DeductedFromDebt);
    }

    [Fact]
    public async Task Paying_a_refund_debits_the_customers_account_and_the_box_and_squares_the_credit()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);
        var (_, note) = await EarlyReturnAsync(h);
        var box = await BoxBalanceAsync(h);
        var debtBefore = await h.TradeDebtAsync();

        var paid = await PaidAsync(vouchers, Refund(note.Id));

        var posted = await h.PostedAsync(LedgerSources.PaymentVoucher);
        Assert.Equal(200_000m, posted[LedgerAccounts.Customers]);
        Assert.Equal(-200_000m, posted[LedgerAccounts.Cash]);
        Assert.Equal(0m, posted.Values.Sum());

        // The 411 line names the customer, so the refund is on their statement…
        Assert.Equal(debtBefore + 200_000m, await h.TradeDebtAsync());
        Assert.Equal(0m, await h.TradeDebtAsync());
        // …and the denormalised balance moves with it: the credit the return left is gone.
        Assert.Equal(0m, await h.BalanceAsync());
        Assert.Equal(box - 200_000m, await BoxBalanceAsync(h));

        var statement = await h.Statements.BuildAsync(Contractor, Day("2026-01-01"), DateTime.UtcNow.AddDays(1));
        Assert.Contains(statement!.Rows, r => r.SourceType == LedgerSources.PaymentVoucher && r.DocumentNumber == paid.Number);
    }

    [Fact]
    public async Task A_refund_never_pays_more_than_the_return_note_still_owes()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);
        var (_, note) = await EarlyReturnAsync(h);

        await RaiseAsync(vouchers, Refund(note.Id, 150_000m));

        var over = await vouchers.CreateAsync(Refund(note.Id, 60_000m), "caissier");
        Assert.Equal(ReceivableFailure.Conflict, over.Failure);
        Assert.Contains("50000", over.Message);

        // The rest is still raisable, and once it is nothing is.
        await RaiseAsync(vouchers, Refund(note.Id, 50_000m));
        var nothing = await vouchers.CreateAsync(Refund(note.Id), "caissier");
        Assert.Equal(ReceivableFailure.Conflict, nothing.Failure);
        Assert.Contains("nothing left to refund", nothing.Message);
    }

    [Fact]
    public async Task A_cancelled_refund_gives_its_share_of_the_note_back()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);
        var (_, note) = await EarlyReturnAsync(h);

        var first = await RaiseAsync(vouchers, Refund(note.Id));
        await vouchers.CancelAsync(first.Id, "Montant à revoir");

        Assert.Equal(200_000m, (await vouchers.RefundableAsync(null, note.Id)).Single().StillOwed);
        Assert.Equal(200_000m, (await RaiseAsync(vouchers, Refund(note.Id))).Amount);
    }

    [Fact]
    public async Task A_refund_for_another_customers_note_is_refused()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var (_, note) = await EarlyReturnAsync(h);
        var refused = await Vouchers(h).CreateAsync(Refund(note.Id, customerId: ReceivableHarness.Other), "x");
        Assert.Equal(ReceivableFailure.Invalid, refused.Failure);
    }

    // ---------- answer 27: a customer who owes the company ----------

    [Fact]
    public async Task Debt_raised_since_the_return_is_taken_off_the_refund_and_only_the_rest_is_paid()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);
        var (_, note) = await EarlyReturnAsync(h);

        // 50,000 billed since: the credit of 200,000 now stands at 150,000.
        Assert.True((await h.RaiseAsync(qty: 1, unitPrice: 50_000m, taxRate: 0m)).Ok);
        Assert.Equal(-150_000m, await h.BalanceAsync());
        Assert.Equal(150_000m, (await vouchers.RefundableAsync(Contractor, null)).Single().Payable);

        var raised = await vouchers.CreateAsync(Refund(note.Id), "caissier");
        Assert.True(raised.Ok, raised.Message);
        Assert.Equal(150_000m, raised.Value.Voucher.Amount);
        Assert.Equal(50_000m, raised.Value.Voucher.DeductedFromDebt);
        Assert.Equal(PaymentVoucherWarnings.TakenOffDebt, raised.Value.Warning!.Code);

        // The deduction closes that much of the note: nothing is left to raise.
        Assert.Equal(0m, (await vouchers.RefundableAsync(null, note.Id)).Single().StillOwed);

        await vouchers.ApproveAsync(raised.Value.Voucher.Id, null, Owner);
        Assert.True((await vouchers.PayAsync(raised.Value.Voucher.Id, null, Cashier)).Ok);
        // Square, not in debt: the deducted part posted nothing because 411 had netted it already.
        Assert.Equal(0m, await h.BalanceAsync());
        Assert.Equal(150_000m, (await h.PostedAsync(LedgerSources.PaymentVoucher))[LedgerAccounts.Customers]);
    }

    [Fact]
    public async Task A_customer_who_owes_more_than_the_refund_is_paid_nothing_and_told_why()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);
        var (_, note) = await EarlyReturnAsync(h);
        await h.RaiseAsync(qty: 1, unitPrice: 250_000m, taxRate: 0m);
        Assert.Equal(50_000m, await h.BalanceAsync());

        var refused = await vouchers.CreateAsync(Refund(note.Id), "caissier");
        Assert.Equal(ReceivableFailure.Conflict, refused.Failure);
        Assert.Contains("taken off that debt", refused.Message);
        Assert.Empty(await h.Db.PaymentVouchers.ToListAsync());
    }

    [Fact]
    public async Task Debt_raised_between_approval_and_payment_stops_the_payment()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);
        var (_, note) = await EarlyReturnAsync(h);

        var voucher = await RaiseAsync(vouchers, Refund(note.Id));
        await vouchers.ApproveAsync(voucher.Id, null, Owner);
        await h.RaiseAsync(qty: 1, unitPrice: 30_000m, taxRate: 0m);

        var refused = await vouchers.PayAsync(voucher.Id, null, Cashier);
        Assert.Equal(ReceivableFailure.Conflict, refused.Failure);
        Assert.Empty(await h.Db.LedgerEntries.Where(e => e.SourceType == LedgerSources.PaymentVoucher).ToListAsync());
    }

    [Fact]
    public async Task A_refund_with_no_note_pays_only_what_the_account_holds_in_the_customers_favour()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);
        // An overpayment: 30,000 received with nothing owed.
        await h.ReceiveAsync(30_000m);

        var over = await vouchers.CreateAsync(Refund(null, 40_000m, Contractor), "caissier");
        Assert.Equal(ReceivableFailure.Conflict, over.Failure);

        var voucher = await RaiseAsync(vouchers, Refund(null, 30_000m, Contractor));
        Assert.Null(voucher.ReturnNoteId);
        Assert.Equal("Ben Ali BTP", voucher.PayeeName);
        // A second one would pay the same credit twice.
        Assert.Equal(ReceivableFailure.Conflict, (await vouchers.CreateAsync(Refund(null, 1m, Contractor), "x")).Failure);
    }

    // ---------- the owner, 2026-10-06: a refund for the unused period of a contract ----------

    private static CreatePaymentVoucherRequest ForContract(Guid contractId, decimal amount = 0m, Guid? customerId = null) =>
        new(Algiers, PaymentVoucherKind.CustomerRefund, amount, "cash", CustomerId: customerId, ContractId: contractId);

    [Fact]
    public async Task Unused_rent_taken_off_a_debt_is_refunded_on_the_contract_once_the_customer_has_paid()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);
        // 300,000 of hire out on 1 March, nothing paid, all back on 11 March: the 200,000 not used
        // comes off the debt, so no return note owes a voucher.
        var contract = (await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            Contractor, Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 100, 30, RentUnitPrice: 100m, DamageUnitPrice: 2_000m)])),
            "admin")).Value!;
        var lineId = contract.Lines.Single().Id;
        Assert.True((await h.Rentals.IssueAsync(contract.Id,
            Driven(new IssueNoteRequest(Algiers, [new IssueNoteLineRequest(lineId, 100)], Day("2026-03-01"))), null, "m")).Ok);
        var back = await h.Rentals.ReturnAsync(contract.Id, Driven(new ReturnNoteRequest(
            Algiers, [new ReturnNoteLineRequest(lineId, QtyOk: 100)], Day("2026-03-11"))), null, "m");
        Assert.Equal(200_000m, back.Value!.Note.ReliefDebtReduction);
        Assert.Empty(await vouchers.RefundableAsync(Contractor, null));

        // The customer then pays the whole 300,000: 200,000 is now theirs.
        Assert.True((await h.ReceiveAsync(300_000m)).Ok);
        var offered = Assert.Single(await vouchers.RefundableContractsAsync(Contractor, null));
        Assert.Equal((contract.Id, 200_000m, 200_000m, 200_000m), (offered.ContractId, offered.UnusedRent, offered.StillOwed, offered.Payable));

        var voucher = await RaiseAsync(vouchers, ForContract(contract.Id));
        Assert.Equal(200_000m, voucher.Amount);
        Assert.Equal(contract.Id, voucher.ContractId);
        Assert.Null(voucher.ReturnNoteId);
        Assert.Equal(Contractor, voucher.CustomerId);
        Assert.Contains(contract.Number, voucher.Purpose);

        // Once raised it is not offered again, and a second one is refused in words.
        Assert.Empty(await vouchers.RefundableContractsAsync(Contractor, null));
        var again = await vouchers.CreateAsync(ForContract(contract.Id), "x");
        Assert.Equal(ReceivableFailure.Conflict, again.Failure);
        Assert.Contains("nothing left to refund", again.Message);
    }

    [Fact]
    public async Task A_contract_refund_counts_what_its_notes_already_raised_and_never_pays_more_than_is_left()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);
        var (contract, note) = await EarlyReturnAsync(h);
        await RaiseAsync(vouchers, Refund(note.Id, 50_000m));

        var offered = (await vouchers.RefundableContractsAsync(null, contract.Id)).Single();
        Assert.Equal((200_000m, 50_000m, 150_000m, 150_000m), (offered.UnusedRent, offered.AlreadyRaised, offered.StillOwed, offered.Payable));

        var over = await vouchers.CreateAsync(ForContract(contract.Id, 150_001m), "x");
        Assert.Equal(ReceivableFailure.Conflict, over.Failure);
        Assert.Contains("150000", over.Message);
        Assert.Equal(150_000m, (await RaiseAsync(vouchers, ForContract(contract.Id, 150_000m))).Amount);
    }

    [Fact]
    public async Task A_contract_refund_is_refused_for_another_customer_or_a_cancelled_contract()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);
        var (contract, _) = await EarlyReturnAsync(h);

        var other = await vouchers.CreateAsync(ForContract(contract.Id, customerId: Guid.NewGuid()), "x");
        Assert.Equal(ReceivableFailure.Invalid, other.Failure);

        var draft = (await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            Contractor, Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 1, 30, RentUnitPrice: 100m, DamageUnitPrice: 2_000m)])),
            "admin")).Value!;
        Assert.True((await h.Rentals.CancelContractAsync(draft.Id, "changed his mind", "admin")).Ok);
        Assert.Equal(ReceivableFailure.NotFound, (await vouchers.CreateAsync(ForContract(draft.Id, 1m), "x")).Failure);
        Assert.Empty(await vouchers.RefundableContractsAsync(null, draft.Id));
    }

    // ---------- answer 23: the receipt's "in exchange for" line ----------

    [Fact]
    public async Task A_receipt_against_a_rental_invoice_defaults_its_purpose_to_the_contract_and_period()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var contract = (await h.Rentals.CreateContractAsync(ContractorSited(new UpsertRentalContractRequest(
            Contractor, Algiers,
            [new RentalContractLineRequest(ReceivableHarness.Scaffold, 100, 2, RentUnitPrice: 100m, DamageUnitPrice: 2_000m)])),
            "admin")).Value!;
        var issued = (await h.Rentals.IssueAsync(contract.Id, Driven(new IssueNoteRequest(
            Algiers, [new IssueNoteLineRequest(contract.Lines.Single().Id, 10)], Day("2026-09-27"))), null, "magasinier")).Value!;
        var invoice = (await h.Receivables.CreateInvoiceAsync(
            new CreateInvoiceRequest(DateTime.UtcNow.AddDays(30), IssueNoteId: issued.Note.Id), "admin")).Value!;

        var expected = $"Location — contrat {contract.Number}";
        Assert.Equal(expected, await h.Receivables.DefaultPurposeAsync([invoice.Id]));

        var receipt = await h.ReceiveAsync(1_000m, invoiceId: invoice.Id);
        Assert.True(receipt.Ok, receipt.Message);
        Assert.Equal(expected, receipt.Value!.Purpose);

        // What the clerk writes wins.
        var typed = await h.Receivables.CreateReceiptAsync(new CreateReceiptRequest(
            Contractor, Algiers, 500m, "cash",
            Allocations: [new ReceiptAllocationRequest(invoice.Id, 500m)], Purpose: "Acompte chantier Bir Mourad Raïs"), "caissier");
        Assert.Equal("Acompte chantier Bir Mourad Raïs", typed.Value!.Purpose);
    }

    [Fact]
    public async Task A_receipt_with_nothing_rental_behind_it_says_nothing_it_does_not_know()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var invoice = (await h.RaiseAsync()).Value!;

        Assert.Null(await h.Receivables.DefaultPurposeAsync([invoice.Id]));
        Assert.Null((await h.ReceiveAsync(1_000m, invoiceId: invoice.Id)).Value!.Purpose);
        Assert.Null((await h.ReceiveAsync(1_000m)).Value!.Purpose);
    }

    // ---------- the chart ----------

    [Fact]
    public void Every_expense_category_posts_to_an_account_the_chart_carries()
    {
        var chart = LedgerAccounts.Default.Select(a => a.Code).ToHashSet();
        Assert.All(PaymentVouchers.ExpenseAccounts.Values, code => Assert.Contains(code, chart));
        // Every one an expense account, except the workers' bonus (owner, 2026-11-08): its cash is held on staff
        // advances until payroll books it as wages, so it is not counted as an expense twice.
        Assert.Equal(LedgerAccounts.StaffAdvances, PaymentVouchers.ExpenseAccounts[PaymentVouchers.WorkersBonus]);
        var expenseCodes = PaymentVouchers.ExpenseAccounts.Where(e => e.Key != PaymentVouchers.WorkersBonus).Select(e => e.Value).ToHashSet();
        Assert.All(LedgerAccounts.Default.Where(a => expenseCodes.Contains(a.Code)),
            a => Assert.Equal(LedgerAccountType.Expense, a.Type));
        Assert.Equal(LedgerAccounts.Default.Count, LedgerAccounts.Default.Select(a => a.Code).Distinct().Count());
    }

    // ---------- answers 19, 20, 26 (2026-10-02): the contract on the voucher ----------

    [Fact]
    public async Task A_category_the_company_marks_must_name_its_contract_and_any_other_may()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);
        var (contract, _) = await EarlyReturnAsync(h);

        Assert.True((await vouchers.SetContractCategoriesAsync(["Transport"])).Ok);
        var categories = await vouchers.CategoriesAsync();
        Assert.True(categories.Single(c => c.Key == "transport").ContractRequired);
        Assert.False(categories.Single(c => c.Key == "purchase").ContractRequired);
        Assert.False((await vouchers.SetContractCategoriesAsync(["lunch"])).Ok);

        var refused = await vouchers.CreateAsync(Expense(category: "transport"), "x");
        Assert.Equal(ReceivableFailure.Invalid, refused.Failure);
        Assert.Contains("contract", refused.Message);

        var named = await RaiseAsync(vouchers, Expense(category: "transport") with { ContractId = contract.Id });
        Assert.Equal(contract.Id, named.ContractId);
        // A purchase needs none, and may still name one.
        Assert.Null((await RaiseAsync(vouchers, Expense(category: "purchase"))).ContractId);
    }

    [Fact]
    public async Task A_day_s_labour_is_paid_on_a_voucher_to_the_wages_account()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var vouchers = Vouchers(h);
        await h.ReceiveAsync(50_000m);

        var voucher = await PaidAsync(vouchers, Expense(4_000m, "dailyLabour") with { PayeeName = "Ouvrier journalier" });
        Assert.Equal(LedgerAccounts.Wages, voucher.ExpenseAccountCode);
        Assert.Equal(4_000m, (await h.PostedAsync(LedgerSources.PaymentVoucher))[LedgerAccounts.Wages]);
    }

    [Fact]
    public async Task A_receipt_for_a_repair_or_a_loss_names_the_contract_and_its_return_note()
    {
        await using var h = await ReceivableHarness.NewAsync();
        var (contract, note) = await EarlyReturnAsync(h);
        CreateReceiptRequest Paid(string kind, Guid? contractId, Guid? noteId) =>
            new(Contractor, Algiers, 5_000m, "cash", Kind: kind, ContractId: contractId, ReturnNoteId: noteId);

        Assert.Equal(ReceivableFailure.Invalid, (await h.Receivables.CreateReceiptAsync(Paid("repair", contract.Id, null), "x")).Failure);
        Assert.Equal(ReceivableFailure.Invalid, (await h.Receivables.CreateReceiptAsync(Paid("loss", null, note.Id), "x")).Failure);
        Assert.Equal(ReceivableFailure.Invalid, (await h.Receivables.CreateReceiptAsync(Paid("gift", null, null), "x")).Failure);

        var repair = await h.Receivables.CreateReceiptAsync(Paid("repair", contract.Id, note.Id), "x");
        Assert.True(repair.Ok, repair.Message);
        Assert.Equal(ReceiptKind.Repair, repair.Value!.Kind);
        Assert.Equal(contract.Id, repair.Value.ContractId);
        Assert.Equal(note.Id, repair.Value.ReturnNoteId);
        Assert.Equal($"Réparation — contrat {contract.Number}, bon {note.Number}", repair.Value.Purpose);

        // A payment may name its contract, and says so on the paper.
        var payment = await h.Receivables.CreateReceiptAsync(Paid("payment", contract.Id, null), "x");
        Assert.True(payment.Ok, payment.Message);
        Assert.Equal($"Location — contrat {contract.Number}", payment.Value!.Purpose);
        Assert.Equal(ReceivableFailure.Invalid,
            (await h.Receivables.CreateReceiptAsync(Paid("payment", contract.Id, note.Id), "x")).Failure);
    }
}
