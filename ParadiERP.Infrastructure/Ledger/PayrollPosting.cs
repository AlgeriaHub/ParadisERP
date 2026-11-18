using ParadiERP.Domain;

namespace ParadiERP.Infrastructure.Ledger;

/// <summary>
/// A run's figures as the ledger needs to see them — the engine's totals, with the one split the
/// engine deliberately does not make.
///
/// <para>
/// <c>PayrollTotals.OtherDeductions</c> is every standing deduction added up, because to the
/// arithmetic of a payslip a deduction is a deduction. To the books they are two different
/// events: one where the company simply pays less wage (<c>DeductionTreatment.ReducesWageCost</c>,
/// which comes off 631) and one where the company withholds money that belongs to somebody else
/// (<c>DeductionTreatment.Other</c>, which is a liability the chart does not yet name). The
/// caller reads the treatments off the run's own settings snapshot and hands both halves here.
/// </para>
/// </summary>
/// <param name="Gross">Pay before anything is withheld; deduction elements are not in it.</param>
/// <param name="WageCostDeductions">Deductions that genuinely reduce the wage bill. Positive.</param>
/// <param name="OtherDeductions">Deductions withheld for a third party. Positive. See the ⚠ below.</param>
public readonly record struct PayrollAccrual(
    decimal Gross,
    decimal WageCostDeductions,
    decimal OtherDeductions,
    decimal SocialEmployee,
    decimal SocialEmployer,
    decimal IncomeTax,
    decimal EmployerLevies,
    decimal AdvancesRecovered,
    decimal Net,
    decimal PaidByVoucher = 0m);

/// <summary>
/// What a payroll document means in the ledger.
///
/// <para>
/// Pure and static like <see cref="SalePosting"/> and <see cref="RentalPosting"/>, for the same
/// reason: these are the rules that decide whether a company's wage bill, its debt to the social
/// fund and the tax it withheld are three readable figures or one lump, and a rule that can be
/// checked without a server is a rule that gets checked.
/// </para>
/// <para>
/// <b>Nothing here posts to 630.</b> That heading stays in the chart because a system account is
/// never removed, and stays empty because "charges de personnel" answers none of the three
/// questions the fund, the tax office and the owner ask.
/// </para>
/// </summary>
public static class PayrollPosting
{
    /// <summary>
    /// The accounts a statutory payment may settle. Anything else is a payment that is not a
    /// payroll liability, and it belongs to whichever module owns that account.
    /// </summary>
    public static IReadOnlyList<string> StatutoryLiabilities { get; } =
    [
        LedgerAccounts.SocialSecurityPayable,
        LedgerAccounts.IncomeTaxWithheld,
        LedgerAccounts.OtherPayrollTaxesPayable
    ];

    /// <summary>
    /// One month's wages accrued for one branch, written when the run is approved.
    ///
    /// <para>
    /// The identity it is built on — and the reason it balances for any figures the engine can
    /// produce — is the engine's own definition of net pay:
    /// <c>Net = Gross − SocialEmployee − IncomeTax − deductions − advances</c>. Substituting it
    /// into the two sides leaves <c>631 + 635 + 641 = 431 + 442 + 447 + 425 + 421</c> with 641
    /// carrying the levies net of the "other" deductions, which is exactly the placeholder below.
    /// </para>
    /// <para>
    /// ⚠ <b>accountant — deductions that are not a wage-cost reduction.</b> A fine, or dues
    /// collected for a third party, is money withheld that the company now owes to somebody. The
    /// account that holds it has not been named (docs/07 §5.9, Q23/Q24), and it is emphatically
    /// not 758 — that is <c>RoundingDifference</c>, a control account this would bury. Until the
    /// answer arrives it is credited to 641, <b>reducing the employer's payroll taxes line</b>:
    /// visibly wrong in the P&amp;L the moment it is large, which is the point — it cannot hide.
    /// When the account is named it enters through the seeder and this one line changes; nothing
    /// already posted moves.
    /// </para>
    /// </summary>
    /// <param name="periodEnd">
    /// The last day of the month being accrued, which is the entry's date. Never "now": a March run
    /// approved on 5 April is a March cost, and dating it by the day somebody pressed the button
    /// leaves March's trial balance with no wages on it and April carrying two months of them.
    /// </param>
    /// <returns>Null when the run has nothing in it at all — a branch with no staff this month.</returns>
    public static PostingDraft? Accrual(
        Guid runId, Guid branchId, string reference, PayrollAccrual totals, string? by, DateTime periodEnd)
    {
        // Net of the deductions the company genuinely saved. Written as one figure rather than a
        // debit and a credit because a wage bill read off 631 has to be what the wages cost.
        var wageCost = totals.Gross - totals.WageCostDeductions;

        var draft = new PostingDraft
        {
            EntryDate = periodEnd,
            BranchId = branchId,
            SourceType = LedgerSources.PayrollRun,
            SourceId = runId,
            Description = $"Payroll {reference}",
            CreatedBy = by
        };

        draft
            .AddIfAny(PostingLine.Signed(LedgerAccounts.Wages, wageCost, memo: reference))
            .AddIfAny(PostingLine.Signed(LedgerAccounts.EmployerSocialContributions, totals.SocialEmployer, memo: reference))
            // Signed, and the one line that can legitimately come out as a credit: the levies the
            // employer owes, less whatever the placeholder above has parked here.
            .AddIfAny(PostingLine.Signed(
                LedgerAccounts.EmployerPayrollTaxes, totals.EmployerLevies - totals.OtherDeductions, memo: reference))
            // Both halves of the contribution, on one account, because one payment settles them.
            .AddIfAny(PostingLine.Signed(
                LedgerAccounts.SocialSecurityPayable, -(totals.SocialEmployee + totals.SocialEmployer), memo: reference))
            .AddIfAny(PostingLine.Signed(LedgerAccounts.IncomeTaxWithheld, -totals.IncomeTax, memo: reference))
            .AddIfAny(PostingLine.Signed(LedgerAccounts.OtherPayrollTaxesPayable, -totals.EmployerLevies, memo: reference))
            // The advance comes back: the asset raised when the cash left is cleared here rather
            // than by a second cash document, because no cash moves when a wage is netted down.
            // A workers' bonus already paid by voucher was booked to 425 when the cash left; it is in the gross above,
            // and comes back here the same way an advance does (owner, 2026-11-08).
            .AddIfAny(PostingLine.Signed(LedgerAccounts.StaffAdvances, -(totals.AdvancesRecovered + totals.PaidByVoucher), memo: reference))
            .AddIfAny(PostingLine.Signed(LedgerAccounts.WagesPayable, -totals.Net, memo: reference));

        // The one case where the identity above does not hold: a payslip whose deductions and tax
        // came to more than the pay. The engine holds that net at zero rather than handing anybody
        // a bill on their own wage slip (and says so, in a warning), so the entry is short by
        // exactly what was not collectable.
        //
        // It lands in suspense — the same plug that keeps a sale from ever being refused for
        // tenders that do not add up. A month of wages must not become unapprovable because one
        // man's advance ran past his pay, and the trial balance shows the figure as something to
        // investigate rather than burying it in a wage account.
        var uncollected = totals.Net - (totals.Gross
            - totals.WageCostDeductions - totals.OtherDeductions
            - totals.SocialEmployee - totals.IncomeTax - totals.AdvancesRecovered - totals.PaidByVoucher);
        draft.AddIfAny(PostingLine.Signed(
            LedgerAccounts.Suspense, uncollected, memo: $"{reference} — net held at zero"));

        return draft.Lines.Count == 0 ? null : draft;
    }

    /// <summary>
    /// Wages handed over: what the company owed its staff is cleared, and the money leaves the
    /// box or the bank.
    /// </summary>
    /// <param name="branchId">The branch whose wage debt is being settled.</param>
    /// <param name="cashBranchId">
    /// The branch whose box actually paid, when it was cash. A crew paid out of the safe at the
    /// main branch is an inter-branch movement, and the credit has to sit where the cash left.
    /// </param>
    /// <param name="paidAtUtc">
    /// The day the money left, which is the day the cash movement carries. The two have to be the
    /// same date or the Z-report and the journal can never be reconciled.
    /// </param>
    public static PostingDraft? Payment(
        Guid paymentId, Guid branchId, string reference, decimal amount,
        PayoutMethod method, Guid? cashBranchId, string? by, DateTime paidAtUtc)
    {
        if (amount <= 0) return null;

        var byBank = method == PayoutMethod.Bank;
        var payingBranch = byBank ? null : cashBranchId;
        var interBranch = payingBranch is Guid paying && paying != branchId;

        return new PostingDraft
        {
            EntryDate = paidAtUtc,
            BranchId = branchId,
            SourceType = LedgerSources.WagePayment,
            SourceId = paymentId,
            Description = $"Wage payment {reference}",
            CreatedBy = by,
            // P0.2: never marked inter-branch — the group report drops such entries whole, and a wage paid out of
            // another branch's box is a real settlement for the group, not a movement between its branches. The paying
            // branch is still named, and its line still sits at that branch.
            IsInterBranch = false,
            CounterpartBranchId = interBranch ? payingBranch : null
        }
            .Add(PostingLine.Dr(LedgerAccounts.WagesPayable, amount, memo: reference))
            .Add(PostingLine.Cr(
                byBank ? LedgerAccounts.Bank : LedgerAccounts.Cash, amount, payingBranch, reference));
    }

    /// <summary>
    /// Cash drawn against a wage before the month closes. The employee's branch carries the
    /// claim; the box that paid carries the outflow, and they are often not the same branch.
    /// </summary>
    /// <param name="grantedAtUtc">The day the cash left — the same date its cash movement carries.</param>
    public static PostingDraft? Advance(
        Guid advanceId, Guid employeeBranchId, string reference, decimal amount,
        PayoutMethod method, Guid? cashBranchId, string? by, DateTime grantedAtUtc)
    {
        if (amount <= 0) return null;

        var byBank = method == PayoutMethod.Bank;
        var payingBranch = byBank ? null : cashBranchId;
        var interBranch = payingBranch is Guid paying && paying != employeeBranchId;

        return new PostingDraft
        {
            EntryDate = grantedAtUtc,
            BranchId = employeeBranchId,
            SourceType = LedgerSources.WageAdvance,
            SourceId = advanceId,
            Description = $"Wage advance {reference}",
            CreatedBy = by,
            // P0.2: never marked inter-branch — the group report drops such entries whole, and a wage paid out of
            // another branch's box is a real settlement for the group, not a movement between its branches. The paying
            // branch is still named, and its line still sits at that branch.
            IsInterBranch = false,
            CounterpartBranchId = interBranch ? payingBranch : null
        }
            .Add(PostingLine.Dr(LedgerAccounts.StaffAdvances, amount, memo: reference))
            .Add(PostingLine.Cr(
                byBank ? LedgerAccounts.Bank : LedgerAccounts.Cash, amount, payingBranch, reference));
    }

    /// <summary>
    /// The fund or the tax office actually being paid. Without it 431, 442 and 447 accumulate
    /// forever and a balance sheet reads as debt that was settled months ago.
    /// </summary>
    /// <param name="accountCode">One of <see cref="StatutoryLiabilities"/>.</param>
    /// <param name="paidAtUtc">The day the transfer went out — the same date its cash movement carries.</param>
    public static PostingDraft? StatutoryPayment(
        Guid paymentId, Guid branchId, string accountCode, string reference, decimal amount,
        PayoutMethod method, Guid? cashBranchId, string? by, DateTime paidAtUtc)
    {
        if (amount <= 0) return null;
        if (!StatutoryLiabilities.Contains(accountCode, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(
                $"Account {accountCode} is not a payroll liability; a statutory payment settles 431, 442 or 447.",
                nameof(accountCode));

        var byBank = method == PayoutMethod.Bank;
        var payingBranch = byBank ? null : cashBranchId;
        var interBranch = payingBranch is Guid paying && paying != branchId;

        return new PostingDraft
        {
            EntryDate = paidAtUtc,
            BranchId = branchId,
            SourceType = LedgerSources.PayrollStatutoryPayment,
            SourceId = paymentId,
            Description = $"Statutory payment {reference}",
            CreatedBy = by,
            // P0.2: never marked inter-branch — the group report drops such entries whole, and a wage paid out of
            // another branch's box is a real settlement for the group, not a movement between its branches. The paying
            // branch is still named, and its line still sits at that branch.
            IsInterBranch = false,
            CounterpartBranchId = interBranch ? payingBranch : null
        }
            .Add(PostingLine.Dr(accountCode, amount, memo: reference))
            .Add(PostingLine.Cr(
                byBank ? LedgerAccounts.Bank : LedgerAccounts.Cash, amount, payingBranch, reference));
    }
}
