using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Payroll;
using Engine = AlgeriaPos.Application.Payroll;

namespace AlgeriaPos.Tests.Payroll;

/// <summary>
/// What a payroll document means in the ledger — docs/07 §5.9.
///
/// <para>
/// No database at all: these are the rules that decide whether the wage bill, the debt to the
/// fund and the tax withheld are three readable figures or one lump, and a rule that can be
/// checked without a server is a rule that gets checked. What the run then does with them is
/// <see cref="PayrollRunTests"/>.
/// </para>
/// <para>
/// Every figure here is synthetic. None of it is anybody's law.
/// </para>
/// </summary>
public class PayrollPostingTests
{
    private static readonly Guid Algiers = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Oran = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Document = Guid.Parse("33333333-3333-3333-3333-333333333333");

    // The dates a payroll document is posted under: the month it belongs to, and the day the money
    // moved. Never "now" — a March run approved in April is a March cost, and the entry and the
    // cash movement for one payout have to carry the same date or nothing reconciles.
    private static readonly DateTime PeriodEnd = new(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime PaidOn = new(2026, 4, 5, 9, 30, 0, DateTimeKind.Utc);

    private static decimal Debits(PostingDraft draft) => draft.Lines.Sum(l => l.Debit);
    private static decimal Credits(PostingDraft draft) => draft.Lines.Sum(l => l.Credit);

    private static decimal On(PostingDraft draft, string code) =>
        draft.Lines.Where(l => l.AccountCode == code).Sum(l => l.Debit - l.Credit);

    // ---------- the accrual ----------

    [Fact]
    public void The_accrual_debits_wages_and_the_employer_contribution_and_credits_staff_social_tax_and_advances()
    {
        // 20 000 gross, 10 % withheld, 20 % employer, 1 000 tax, 500 already advanced.
        var totals = new PayrollAccrual(
            Gross: 20_000m, WageCostDeductions: 0m, OtherDeductions: 0m,
            SocialEmployee: 2_000m, SocialEmployer: 4_000m, IncomeTax: 1_000m,
            EmployerLevies: 0m, AdvancesRecovered: 500m, Net: 16_500m);

        var draft = PayrollPosting.Accrual(Document, Algiers, "RUN-000001", totals, "admin", PeriodEnd)!;

        Assert.Equal(20_000m, On(draft, LedgerAccounts.Wages));
        Assert.Equal(4_000m, On(draft, LedgerAccounts.EmployerSocialContributions));
        // Both halves of the contribution on one account, because one payment settles them.
        Assert.Equal(-6_000m, On(draft, LedgerAccounts.SocialSecurityPayable));
        Assert.Equal(-1_000m, On(draft, LedgerAccounts.IncomeTaxWithheld));
        // The asset raised when the cash left is cleared here: no cash moves when a wage is
        // netted down, so a second cash document would be an invention.
        Assert.Equal(-500m, On(draft, LedgerAccounts.StaffAdvances));
        Assert.Equal(-16_500m, On(draft, LedgerAccounts.WagesPayable));
        Assert.Equal(Debits(draft), Credits(draft));
    }

    [Fact]
    public void Employer_levies_post_only_when_they_are_configured()
    {
        var without = PayrollPosting.Accrual(Document, Algiers, "RUN-000001", new PayrollAccrual(
            10_000m, 0m, 0m, 1_000m, 2_000m, 0m, EmployerLevies: 0m, AdvancesRecovered: 0m, Net: 9_000m), null, PeriodEnd)!;
        Assert.DoesNotContain(without.Lines, l => l.AccountCode == LedgerAccounts.EmployerPayrollTaxes);
        Assert.DoesNotContain(without.Lines, l => l.AccountCode == LedgerAccounts.OtherPayrollTaxesPayable);

        var with = PayrollPosting.Accrual(Document, Algiers, "RUN-000001", new PayrollAccrual(
            10_000m, 0m, 0m, 1_000m, 2_000m, 0m, EmployerLevies: 300m, AdvancesRecovered: 0m, Net: 9_000m), null, PeriodEnd)!;
        // A cost on one side and a debt on the other: the levy is the company's, never the
        // employee's, so it cannot touch the net.
        Assert.Equal(300m, On(with, LedgerAccounts.EmployerPayrollTaxes));
        Assert.Equal(-300m, On(with, LedgerAccounts.OtherPayrollTaxesPayable));
        Assert.Equal(-9_000m, On(with, LedgerAccounts.WagesPayable));
        Assert.Equal(Debits(with), Credits(with));
    }

    [Fact]
    public void A_deduction_that_reduces_the_wage_cost_comes_off_631_and_never_becomes_income()
    {
        var draft = PayrollPosting.Accrual(Document, Algiers, "RUN-000001", new PayrollAccrual(
            Gross: 10_000m, WageCostDeductions: 400m, OtherDeductions: 0m,
            SocialEmployee: 1_000m, SocialEmployer: 2_000m, IncomeTax: 0m,
            EmployerLevies: 0m, AdvancesRecovered: 0m, Net: 8_600m), null, PeriodEnd)!;

        Assert.Equal(9_600m, On(draft, LedgerAccounts.Wages));
        Assert.DoesNotContain(draft.Lines, l => l.AccountCode == LedgerAccounts.RoundingDifference);
        Assert.Equal(Debits(draft), Credits(draft));
    }

    [Fact]
    public void An_other_deduction_lands_on_the_visible_placeholder_and_never_on_the_rounding_account()
    {
        // ⚠ The account for a deduction withheld for a third party is not named yet (§5.9, Q24).
        // Until it is, it reduces 641 — wrong in a way that shows, which is the point. It must
        // never reach 758: that is RoundingDifference, a control account this would bury.
        var draft = PayrollPosting.Accrual(Document, Algiers, "RUN-000001", new PayrollAccrual(
            Gross: 10_000m, WageCostDeductions: 0m, OtherDeductions: 250m,
            SocialEmployee: 1_000m, SocialEmployer: 2_000m, IncomeTax: 0m,
            EmployerLevies: 0m, AdvancesRecovered: 0m, Net: 8_750m), null, PeriodEnd)!;

        Assert.Equal(-250m, On(draft, LedgerAccounts.EmployerPayrollTaxes));
        Assert.DoesNotContain(draft.Lines, l => l.AccountCode == LedgerAccounts.RoundingDifference);
        Assert.Equal(Debits(draft), Credits(draft));
    }

    [Fact]
    public void A_net_held_at_zero_leaves_what_was_not_collectable_in_suspense_and_still_balances()
    {
        // The engine never hands anybody a bill on their own wage slip: a payslip whose
        // deductions ran past the pay is held at zero and flagged. The entry is then short by
        // exactly what could not be collected, and that difference has to go somewhere visible —
        // never into a wage account, and never into a month nobody can approve.
        var draft = PayrollPosting.Accrual(Document, Algiers, "RUN-000001", new PayrollAccrual(
            Gross: 10_000m, WageCostDeductions: 0m, OtherDeductions: 12_000m,
            SocialEmployee: 1_000m, SocialEmployer: 2_000m, IncomeTax: 0m,
            EmployerLevies: 0m, AdvancesRecovered: 0m, Net: 0m), null, PeriodEnd)!;

        Assert.Equal(3_000m, On(draft, LedgerAccounts.Suspense));
        Assert.Equal(0m, On(draft, LedgerAccounts.WagesPayable));
        Assert.Equal(Debits(draft), Credits(draft));
    }

    [Fact]
    public void The_accrual_balances_for_any_totals_the_engine_can_produce()
    {
        // Property-style, driven through the engine itself rather than through invented totals:
        // what has to hold is that whatever the engine computes, the entry made from it balances.
        var random = new Random(20260925);
        var money = new RegionalSettings { CurrencyDecimals = 2 };

        for (var attempt = 0; attempt < 200; attempt++)
        {
            var settings = Profile(random);
            var inputs = Enumerable.Range(0, random.Next(1, 6))
                .Select(_ => Employee(random))
                .ToList();

            var computation = Engine.PayrollEngine.Compute(settings, money, inputs);
            var totals = computation.Totals;

            var (wageCost, other) = PayrollDocuments.SplitDeductions(
                computation.Lines
                    .SelectMany(l => l.Elements)
                    .Where(e => e.Kind == Engine.PayrollElementKind.Deduction)
                    .Select(e => (e.Key, e.Amount)),
                settings);

            var draft = PayrollPosting.Accrual(Document, Algiers, "RUN-000001", new PayrollAccrual(
                totals.Gross, wageCost, other, totals.SocialEmployee, totals.SocialEmployer,
                totals.IncomeTax, totals.EmployerLevies, totals.AdvancesRecovered, totals.Net), null, PeriodEnd);

            if (draft is null) continue;
            Assert.Equal(Debits(draft), Credits(draft));
            // A negative debit is a credit written badly, and LedgerPoster refuses one; Signed()
            // exists so the placeholder on 641 can go negative without ever producing one.
            Assert.All(draft.Lines, line => Assert.True(line.Debit >= 0 && line.Credit >= 0));
        }
    }

    private static Engine.PayrollSettings Profile(Random random)
    {
        var settings = new Engine.PayrollSettings
        {
            CountryCode = "ZZ",
            SocialEmployeeRate = random.Next(0, 30),
            SocialEmployerRate = random.Next(0, 30),
            IncomeTaxMode = Engine.IncomeTaxMode.Scale,
            IncomeTaxBrackets =
            [
                new Engine.TaxBracket { UpTo = 1_000m, Rate = 0m },
                new Engine.TaxBracket { UpTo = 2_000m, Rate = 10m },
                new Engine.TaxBracket { UpTo = null, Rate = 20m }
            ],
            WorkingDaysPerMonth = 20m,
            HoursPerDay = 10m,
            EmployeeContributionReducesTaxBase = random.Next(2) == 0,
            Allowances =
            [
                new Engine.AllowanceDef
                {
                    Key = "transport", Value = random.Next(0, 2_000),
                    SubjectToCnas = random.Next(2) == 0, SubjectToIrg = random.Next(2) == 0
                }
            ],
            Deductions =
            [
                new Engine.DeductionDef
                {
                    Key = "canteen", Value = random.Next(0, 800),
                    Treatment = Engine.DeductionTreatment.ReducesWageCost
                },
                new Engine.DeductionDef
                {
                    Key = "fine", Value = random.Next(0, 800),
                    Treatment = Engine.DeductionTreatment.Other
                }
            ],
            EmployerLevies = random.Next(2) == 0
                ? [new Engine.LevyDef { Key = "training", Rate = random.Next(0, 5) }]
                : [],
            Confirmed = true,
            ConfirmedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        Engine.PayrollSettingsValidator.NormalizeAndValidate(settings);
        return settings;
    }

    private static Engine.EmployeeInput Employee(Random random)
    {
        var monthly = random.Next(2) == 0;
        var attendance = new Engine.AttendanceSummary(
            random.Next(0, 22), random.Next(0, 3), random.Next(0, 4),
            new Dictionary<string, decimal>(), Engine.AttendanceSource.Declared);

        return new Engine.EmployeeInput(
            Guid.NewGuid(),
            monthly ? Engine.PayBasis.Monthly : Engine.PayBasis.Daily,
            monthly ? random.Next(1_000, 60_000) : random.Next(100, 3_000),
            attendance,
            OpenAdvances: random.Next(2) == 0
                ? [new Engine.OpenAdvance(Guid.NewGuid(), random.Next(0, 20_000))]
                : null);
    }

    // ---------- the money documents ----------

    [Fact]
    public void Paying_in_cash_credits_the_paying_branchs_box_and_by_bank_credits_the_bank()
    {
        var cash = PayrollPosting.Payment(
            Document, Algiers, "WP-000001", 5_000m, PayoutMethod.Cash, Algiers, "admin", PaidOn)!;
        Assert.Equal(5_000m, On(cash, LedgerAccounts.WagesPayable));
        Assert.Equal(-5_000m, On(cash, LedgerAccounts.Cash));
        Assert.False(cash.IsInterBranch);

        var bank = PayrollPosting.Payment(
            Document, Algiers, "WP-000002", 5_000m, PayoutMethod.Bank, null, "admin", PaidOn)!;
        // A bank is not a drawer: nothing here touches a cash box, and the branch's box report
        // must not show a movement that never happened.
        Assert.Equal(-5_000m, On(bank, LedgerAccounts.Bank));
        Assert.DoesNotContain(bank.Lines, l => l.AccountCode == LedgerAccounts.Cash);
    }

    [Fact]
    public void A_crew_paid_out_of_another_branchs_safe_names_that_branch_and_is_credited_where_the_cash_left()
    {
        var draft = PayrollPosting.Payment(
            Document, Oran, "WP-000001", 3_000m, PayoutMethod.Cash, Algiers, "admin", PaidOn)!;

        // P0.2: a real settlement for the group, never marked inter-branch (the group report would drop it whole).
        Assert.False(draft.IsInterBranch);
        Assert.Equal(Algiers, draft.CounterpartBranchId);
        // The debit carries no branch of its own, so it falls to the document's — Oran, whose
        // wage debt is being cleared; the credit names the branch whose cash actually left, or a
        // single-branch balance sheet cannot balance.
        Assert.Null(draft.Lines.Single(l => l.AccountCode == LedgerAccounts.WagesPayable).BranchId);
        Assert.Equal(Algiers, draft.Lines.Single(l => l.AccountCode == LedgerAccounts.Cash).BranchId);
    }

    [Fact]
    public void An_advance_is_an_asset_at_the_employees_branch_and_cash_out_at_the_box_that_paid()
    {
        var draft = PayrollPosting.Advance(
            Document, Oran, "ADV-000001", 2_000m, PayoutMethod.Cash, Algiers, "foreman", PaidOn)!;

        Assert.Equal(2_000m, On(draft, LedgerAccounts.StaffAdvances));
        Assert.Equal(-2_000m, On(draft, LedgerAccounts.Cash));
        Assert.False(draft.IsInterBranch);
        Assert.Equal(Algiers, draft.CounterpartBranchId);
        Assert.Equal(Algiers, draft.Lines.Single(l => l.AccountCode == LedgerAccounts.Cash).BranchId);
    }

    [Fact]
    public void A_statutory_payment_settles_a_payroll_liability_and_refuses_any_other_account()
    {
        var draft = PayrollPosting.StatutoryPayment(
            Document, Algiers, LedgerAccounts.SocialSecurityPayable, "PSP-000001", 6_000m,
            PayoutMethod.Bank, null, "comptable", PaidOn)!;

        Assert.Equal(6_000m, On(draft, LedgerAccounts.SocialSecurityPayable));
        Assert.Equal(-6_000m, On(draft, LedgerAccounts.Bank));

        // Settling 421 is a wage payment and has its own document; settling 411 is not payroll at
        // all. Naming the account here would let either be posted as a statutory remittance.
        Assert.Throws<ArgumentException>(() => PayrollPosting.StatutoryPayment(
            Document, Algiers, LedgerAccounts.WagesPayable, "PSP-000002", 100m,
            PayoutMethod.Bank, null, "comptable", PaidOn));
    }

    // ---------- the chart ----------

    [Fact]
    public void Every_account_payroll_posts_to_is_in_the_default_chart_and_none_of_them_is_the_630_heading()
    {
        var drafts = new[]
        {
            PayrollPosting.Accrual(Document, Algiers, "RUN-000001", new PayrollAccrual(
                10_000m, 100m, 200m, 1_000m, 2_000m, 300m, 400m, 500m, 7_900m), null, PeriodEnd),
            PayrollPosting.Payment(Document, Algiers, "WP-000001", 1_000m, PayoutMethod.Cash, Algiers, null, PaidOn),
            PayrollPosting.Payment(Document, Algiers, "WP-000002", 1_000m, PayoutMethod.Bank, null, null, PaidOn),
            PayrollPosting.Advance(Document, Algiers, "ADV-000001", 1_000m, PayoutMethod.Cash, Algiers, null, PaidOn),
            PayrollPosting.StatutoryPayment(
                Document, Algiers, LedgerAccounts.IncomeTaxWithheld, "PSP-000001", 1_000m,
                PayoutMethod.Bank, null, null, PaidOn)
        };

        var chart = LedgerAccounts.Default.Select(a => a.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var used = drafts.Where(d => d is not null).SelectMany(d => d!.Lines).Select(l => l.AccountCode).ToList();

        // LedgerPoster throws on a code the chart does not carry, and it throws at the moment the
        // entry is written — which for payroll is month end, on a Thursday, with the crew waiting.
        Assert.All(used, code => Assert.Contains(code, chart));
        // 630 is a heading: "charges de personnel" answers none of the three questions the fund,
        // the tax office and the owner ask, so nothing posts to it.
        Assert.DoesNotContain("630", used);
    }
}
