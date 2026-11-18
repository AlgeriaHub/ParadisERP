using System.Text.Json;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Hr;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Engine = ParadisERP.Application.Payroll;

namespace ParadisERP.Infrastructure.Payroll;

/// <summary>Why a payroll transition could not happen, in the shape an HTTP layer needs.</summary>
public enum PayrollFailure
{
    NotFound,
    Invalid,
    Conflict,
    /// <summary>The one refusal payroll has: a tax scale nobody has entered. Carries <c>Refusal</c>.</summary>
    Refused
}

/// <summary>
/// A run, or the reason there isn't one — and, separately, anything the user should be told about
/// a transition that <i>did</i> happen.
///
/// <para>
/// Mirrors <c>ReceivableResult</c>, warning field and all, for the reason CLAUDE.md gives: a
/// warning travels beside a document, never in place of one. Payroll's warnings — a foreman's
/// missing sheet, a net clamped at zero, a wage under the floor — are on the lines, and folding
/// any of them into <see cref="Failure"/> is how a warning quietly becomes a refusal and a month
/// of wages stops being payable.
/// </para>
/// </summary>
/// <param name="Code">
/// A refusal the screen can say in the reader's own language, resolved as <c>hr.problem.{code}</c>
/// with <paramref name="Args"/> filled in. Null where the sentence is all there is — a curl user,
/// the smoke script and the log read <see cref="Message"/>, and a screen that meets a code it has
/// no wording for falls back to it.
/// </param>
public sealed record PayrollResult<T>(
    T? Value,
    PayrollFailure? Failure = null,
    string? Message = null,
    Engine.RunRefusal? Refusal = null,
    string? Warning = null,
    string? Code = null,
    IReadOnlyDictionary<string, string>? Args = null)
{
    public bool Ok => Failure is null;

    public static PayrollResult<T> Success(T value, string? warning = null) => new(value, Warning: warning);
    public static PayrollResult<T> NotFound(string message) => new(default, PayrollFailure.NotFound, message);

    public static PayrollResult<T> Invalid(
        string message, string? code = null, IReadOnlyDictionary<string, string>? args = null) =>
        new(default, PayrollFailure.Invalid, message, Code: code, Args: args);

    public static PayrollResult<T> Conflict(
        string message, string? code = null, IReadOnlyDictionary<string, string>? args = null) =>
        new(default, PayrollFailure.Conflict, message, Code: code, Args: args);

    public static PayrollResult<T> Refused(Engine.RunRefusal refusal) =>
        new(default, PayrollFailure.Refused,
            "The income-tax scale has not been configured, so nothing can be computed yet.", refusal);
}

/// <summary>
/// The refusal codes payroll's money routes name, beside the English sentence they carry.
///
/// <para>
/// Every one of these is met routinely by a manager reading Arabic or French, and every one of
/// them quotes a figure or a document number — so the sentence cannot simply be translated at the
/// screen: it has to be rebuilt there, from the parameters. Same envelope as
/// <c>HrProblems</c>, which the attendance and employee routes already answer in.
/// </para>
/// </summary>
public static class PayrollProblems
{
    /// <summary>The branch already has a live run for that month. Args: <c>run</c>, <c>month</c>, <c>year</c>.</summary>
    public const string PeriodTaken = "run_period_taken";

    /// <summary>A payment still stands against the run. Args: <c>payment</c>.</summary>
    public const string RunPaid = "run_paid_by_payment";

    /// <summary>Adjustments were typed after the figures were computed. Args: <c>run</c>.</summary>
    public const string NotRecomputed = "run_recompute_needed";

    /// <summary>An employee's base pay changed after the figures were computed. Args: <c>run</c>.</summary>
    public const string PayMoved = "run_pay_moved";

    /// <summary>The advances the run allocated have moved since. Args: <c>run</c>.</summary>
    public const string AdvancesMoved = "run_advances_moved";

    /// <summary>More than the payslip still owes. Args: <c>name</c>, <c>over</c>, <c>owed</c>, <c>net</c>.</summary>
    public const string Overpaid = "wage_overpaid";
}

/// <summary>
/// The payroll run's lifecycle: create, compute, unlock, approve, reverse — docs/07 §5.7.
///
/// <para>
/// <b>One live run per branch per month.</b> Uniqueness is over non-reversed runs, checked here
/// inside the write scope on both providers. SQLite carries a filtered unique index as a second
/// pair of hands; MySQL 8 has no partial index, so on that provider this check <i>is</i> the rule.
/// </para>
/// <para>
/// <b>The engine stays pure and this class stays dull.</b> Everything arithmetic — the base wage,
/// the bases, the contributions, the tax, the advance recovery — is
/// <c>ParadisERP.Application.Payroll.PayrollEngine</c>, checkable against a hand calculation with
/// no server running. What lives here is the database, the ledger and the state machine, and the
/// moment a rule about money starts being written in this file it has been written in the wrong
/// one.
/// </para>
/// <para>
/// <b>Compute is short on purpose.</b> <c>SubscriptionWriteFilter</c> opens the transaction and
/// holds the sync-counter row for the whole handler, so a compute that issued a query per
/// employee would stall every till pushing at month end. A handful of bulk reads — the staff, their
/// allowances, the month's attendance, the open advances and what other runs have claimed of them —
/// then the arithmetic in memory, two deletes and one insert.
/// </para>
/// </summary>
public sealed class PayrollDocuments(
    PosDbContext db,
    LedgerPoster ledger,
    PayrollSettingsStore settings,
    IRegionalSettingsProvider regionalSettings,
    EmployeeDirectory employees,
    AttendanceSheet attendance)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---------- create ----------

    /// <summary>
    /// Opens a month for a branch. Refused while a live run already holds that pair — after a
    /// reversal the month is open again, and the reversed run stays where it is with both of its
    /// ledger links on it.
    /// </summary>
    public async Task<PayrollResult<PayrollRun>> CreateAsync(
        Guid branchId, int year, int month, string? by, CancellationToken ct = default)
    {
        if (month is < 1 or > 12) return PayrollResult<PayrollRun>.Invalid("A period is a calendar month, 1 to 12.");
        if (year is < 2000 or > 2200) return PayrollResult<PayrollRun>.Invalid("That is not a payroll year.");

        var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == branchId && !b.IsDeleted, ct);
        if (branch is null) return PayrollResult<PayrollRun>.NotFound("Branch not found.");
        if (!branch.IsActive) return PayrollResult<PayrollRun>.Invalid("That branch is not active.");

        await using var scope = await db.BeginWriteScopeAsync(ct);

        // Non-reversed only. A plain "does a run exist" check here would let one reversed month
        // block its branch from ever being run again, which is exactly what the filtered index
        // on SQLite is filtered for.
        var live = await db.PayrollRuns.AsNoTracking()
            .Where(r => r.BranchId == branchId && r.PeriodYear == year && r.PeriodMonth == month
                        && r.Status != PayrollRunStatus.Reversed)
            .Select(r => r.Number)
            .FirstOrDefaultAsync(ct);
        if (live is not null)
            return PayrollResult<PayrollRun>.Conflict(
                $"This branch already has a payroll run for {month:D2}/{year} ({live}). Reverse it before opening another.",
                PayrollProblems.PeriodTaken,
                new Dictionary<string, string>
                {
                    ["run"] = live, ["month"] = month.ToString("D2"), ["year"] = year.ToString()
                });

        var run = new PayrollRun
        {
            BranchId = branchId,
            PeriodYear = year,
            PeriodMonth = month,
            Status = PayrollRunStatus.Draft,
            // New payroll pays the contractual wage and ordinary deductions, but CNAS and IRG
            // stay outside the salary slip. The flag freezes that choice with this document.
            CalculateStatutoryDeductions = false,
            CurrencyDecimals = regionalSettings.Get().CurrencyDecimals,
            CreatedBy = Trim(by, 200)
        };
        // Counted inside the scope, like every other document number in this codebase: outside it
        // two runs opened in the same second take the same number.
        run.Number = Numbered("RUN", await db.PayrollRuns.CountAsync(ct) + 1);

        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);

        return PayrollResult<PayrollRun>.Success(run);
    }

    // ---------- compute ----------

    /// <summary>
    /// Works out what everybody is paid, and locks the month so the sheet stops moving under it.
    ///
    /// <para>
    /// Recomputing is ordinary: an adjustment is added, a sheet is corrected, the run is computed
    /// again and the previous lines are replaced. The one thing it may not do is remove a line
    /// somebody has already been paid against — the <c>Restrict</c> on <c>WagePaymentLine</c>
    /// would refuse it at the database anyway, and a foreign-key error is not a sentence anybody
    /// can act on.
    /// </para>
    /// </summary>
    public async Task<PayrollResult<PayrollRun>> ComputeAsync(
        Guid runId, string? by, CancellationToken ct = default)
    {
        var stored = await settings.ReadAsync(ct);
        var profile = stored.Settings;

        var regional = regionalSettings.Get();

        await using var scope = await db.BeginWriteScopeAsync(ct);

        var run = await db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return PayrollResult<PayrollRun>.NotFound("Payroll run not found.");
        if (run.Status is not (PayrollRunStatus.Draft or PayrollRunStatus.Locked))
            return PayrollResult<PayrollRun>.Conflict(
                "Only a draft or locked run is computed. An approved run is corrected by reversing it.");

        // The tax scale is relevant only to runs that still calculate IRG. Test after the run is
        // loaded: this is a run-level historical rule, not today's company preference.
        if (Engine.PayrollEngine.Refusal(profile, run.CalculateStatutoryDeductions) is { } refusal)
            return PayrollResult<PayrollRun>.Refused(refusal);

        if (await PaidByAsync(run.Id, ct) is string payment)
            return PayrollResult<PayrollRun>.Conflict(
                $"Payment {payment} has already paid part of this run, so its lines cannot be replaced. " +
                "Reverse that payment first.",
                PayrollProblems.RunPaid, new Dictionary<string, string> { ["payment"] = payment });

        var period = new AttendancePeriod(run.PeriodYear, run.PeriodMonth);

        // ---------- the reads ----------
        //
        // Every one of them is a set, not a loop: a branch with nine hundred staff is one query
        // each. They are written against a sub-select of the employee ids rather than a list of
        // nine hundred parameters, which is the same thing said in a way the database can plan.

        // Who this branch pays this month is EmployeeDirectory's single answer, the one the
        // attendance sheet opens with. It deliberately does not test IsActive on its own: leaving
        // clears that flag, so a run that repeated the test here dropped the final payslip of
        // everybody who ever left mid-month — silently, and for the month they worked most of.
        var payable = employees.Payable(run.BranchId, period);
        var staff = payable.Select(e => e.Id);

        // The branch that holds them at compute time pays them whole — there is no dated branch
        // history, and half a payslip at each of two branches is not a thing anybody wants to read.
        var people = await payable.OrderBy(e => e.Number).ToListAsync(ct);

        var allowances = await db.EmployeeAllowances.AsNoTracking()
            .Where(a => staff.Contains(a.EmployeeId))
            .ToListAsync(ct);

        // The one answer to "how many days did he work", whichever way the month was captured —
        // declared totals, ticked days, or clock punches nobody has turned into day rows. A second
        // reduction written here read neither the punches nor a half day, so a badge-clocked month
        // came out unpaid and a four-hour day was paid as eight.
        var summaries = await attendance.SummariesAsync(people, period, ct);

        // Ordered by the number they were recorded under rather than by the day the money left:
        // a number is minted in order and never moves, so an advance entered between compute and
        // approve sorts *after* the prefix this allocation took, whatever date it carries. The
        // same order is replayed at Approve.
        var advances = await db.WageAdvances.AsNoTracking()
            .Where(a => staff.Contains(a.EmployeeId) && a.Status == WageAdvanceStatus.Open)
            .OrderBy(a => a.Number).ThenBy(a => a.Id)
            .ToListAsync(ct);

        // What other computed-but-unapproved runs have already promised themselves out of those
        // same balances. A recovery only reaches WageAdvance.Recovered at Approve, so two months
        // computed before either is approved would each take the whole outstanding — the employee
        // docked twice for one loan and 425 credited money that was never an asset.
        var claimed = await ClaimedElsewhereAsync(run.Id, staff, null, ct);

        var adjustments = await db.PayrollAdjustments.AsNoTracking()
            .Where(a => a.RunId == run.Id)
            .ToListAsync(ct);

        var bonuses = await VoucherBonusesAsync(run, staff, ct);

        // ---------- the arithmetic, in memory ----------

        var allowancesBy = allowances.GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());
        var advancesBy = advances.GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());
        var adjustmentsBy = adjustments.GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());
        var bonusesBy = bonuses.GroupBy(b => b.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());

        var inputs = people.Select(employee => new Engine.EmployeeInput(
            employee.Id,
            (Engine.PayBasis)(int)employee.PayBasis,
            employee.BaseRate,
            summaries.GetValueOrDefault(employee.Id, Engine.AttendanceSummary.Nothing),
            allowancesBy.GetValueOrDefault(employee.Id)?
                .Select(a => new Engine.EmployeeAllowanceOverride(a.AllowanceKey, a.Value, a.IsEnabled)).ToList(),
            Outstanding(advancesBy.GetValueOrDefault(employee.Id), claimed.GetValueOrDefault(employee.Id)),
            adjustmentsBy.GetValueOrDefault(employee.Id)?
                .Select(a => new Engine.PayrollAdjustmentInput(a.Key, a.Amount, a.InSocialBase, a.InTaxBase)).ToList(),
            bonusesBy.GetValueOrDefault(employee.Id)?
                .Select(b => new Engine.VoucherBonusInput(b.Id, b.Amount, profile.VoucherBonusInSocialBase, profile.VoucherBonusInTaxBase))
                .ToList()))
            .ToList();

        var computation = Engine.PayrollEngine.Compute(profile, regional, inputs, run.CalculateStatutoryDeductions);
        var byEmployee = people.ToDictionary(e => e.Id);

        // ---------- the write ----------

        // Elements first: the cascade would take them with the lines on both providers, but only
        // while foreign keys are enforced, and a set-based delete that silently depends on a
        // PRAGMA is a thing that works until it doesn't.
        var lineIds = db.PayrollRunLines.Where(l => l.RunId == run.Id).Select(l => l.Id);
        await db.PayrollRunLineElements.Where(e => lineIds.Contains(e.LineId)).ExecuteDeleteAsync(ct);
        await db.PayrollRunLines.Where(l => l.RunId == run.Id).ExecuteDeleteAsync(ct);

        var detecting = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var computed in computation.Lines)
            {
                var employee = byEmployee[computed.EmployeeId];
                var line = new PayrollRunLine
                {
                    RunId = run.Id,
                    EmployeeId = employee.Id,
                    // Snapshots, like the invoice's buyer block: a corrected spelling or a transfer
                    // next year must not change what last March's paper said.
                    EmployeeNumberSnapshot = employee.Number,
                    EmployeeNameSnapshot = employee.FullName,
                    SocialNumberSnapshot = employee.SocialSecurityNumber,
                    BranchIdSnapshot = employee.BranchId,
                    PayBasis = employee.PayBasis,
                    BaseRate = computed.BaseRate,
                    DaysPresent = computed.DaysPresent,
                    PaidLeaveDays = computed.PaidLeaveDays,
                    UnpaidAbsenceDays = computed.UnpaidAbsenceDays,
                    AttendanceSource = (AttendanceSource)(int)computed.AttendanceSource,
                    Gross = computed.Gross,
                    SocialBase = computed.SocialBase,
                    SocialEmployee = computed.SocialEmployee,
                    SocialEmployer = computed.SocialEmployer,
                    TaxBase = computed.TaxBase,
                    IncomeTax = computed.IncomeTax,
                    AdvanceRecovered = computed.AdvanceRecovered,
                    PaidByVoucher = computed.PaidByVoucher,
                    OtherDeductions = computed.OtherDeductions,
                    Net = computed.Net,
                    // The codes, and only the codes. Every figure a warning quotes is on the line
                    // already — the gross a minimum-wage warning is about, the clamped base, the
                    // net that was held at zero — so storing them a second time would be a second
                    // copy that a recompute could leave disagreeing with the first.
                    WarningsJson = computed.Warnings.Count == 0
                        ? null
                        : JsonSerializer.Serialize(
                            computed.Warnings.Select(w => w.Code).Distinct(StringComparer.Ordinal).ToList(), Json)
                };

                foreach (var element in computed.Elements)
                    line.Elements.Add(new PayrollRunLineElement
                    {
                        LineId = line.Id,
                        Key = element.Key,
                        Kind = (PayrollElementKind)(int)element.Kind,
                        Qty = element.Qty,
                        Rate = element.Rate,
                        Amount = element.Amount,
                        // Frozen from the definition: which allowance sits in which base is the
                        // accountant's answer, and it changes.
                        InSocialBase = element.InSocialBase,
                        InTaxBase = element.InTaxBase,
                        SortOrder = element.SortOrder
                    });

                db.PayrollRunLines.Add(line);
            }
        }
        finally
        {
            // Restored before the run's own fields are touched. With detection off, a change to
            // an already-tracked row is invisible to SaveChanges — the lines would be written and
            // the header they belong to would stay at its old totals, and Draft.
            db.ChangeTracker.AutoDetectChangesEnabled = detecting;
        }

        var totals = computation.Totals;
        run.HeadCount = totals.HeadCount;
        run.Gross = totals.Gross;
        run.SocialBase = totals.SocialBase;
        run.SocialEmployee = totals.SocialEmployee;
        run.SocialEmployer = totals.SocialEmployer;
        run.TaxBase = totals.TaxBase;
        run.IncomeTax = totals.IncomeTax;
        run.EmployerLevies = totals.EmployerLevies;
        run.OtherDeductions = totals.OtherDeductions;
        run.AdvancesRecovered = totals.AdvancesRecovered;
        run.PaidByVoucher = totals.PaidByVoucher;
        run.Net = totals.Net;

        // The figures this run was computed against, kept whole. A rate changed in March must not
        // move February's closed payslips, and an inspector years later has to be able to be shown
        // why a figure is what it is.
        run.SettingsSnapshotJson = JsonSerializer.Serialize(profile, Json);
        run.CurrencyDecimals = regional.CurrencyDecimals;
        // Frozen, never read live: a run computed under placeholders goes on saying so after the
        // admin confirms them.
        run.IsProvisional = computation.IsProvisional;
        run.ComputedAtUtc = DateTime.UtcNow;
        run.LockedBy = Trim(by, 200);
        run.Status = PayrollRunStatus.Locked;

        await db.SaveChangesAsync(ct);

        await scope.CommitAsync(ct);

        // A code, not a sentence: every line warning is already a code, and the screen says
        // `hr.warning.{code}` in the reader's own language. An English sentence here is the one
        // place an Arabic manager would have been shown English.
        return PayrollResult<PayrollRun>.Success(
            run,
            run.IsProvisional ? Engine.PayrollWarnings.SettingsUnconfirmed : null);
    }

    /// <summary>
    /// The workers' bonuses paid by voucher that this run puts on its payslips (owner, 2026-11-08), assigned to it here:
    /// every line of a paid voucher, for a person this run pays, of its month or an earlier one, that no other live run
    /// holds. A run that is computed, approved or paid holds its lines; a draft or reversed one binds nothing, as with
    /// advances. Lines this run held and no longer takes (the person left its staff) are released.
    /// </summary>
    private async Task<List<PaymentVoucherBonusLine>> VoucherBonusesAsync(PayrollRun run, IQueryable<Guid> staff, CancellationToken ct)
    {
        var year = run.PeriodYear;
        var month = run.PeriodMonth;
        // The staff ids first, as a list: the staff query is no-tracking, and composed into this one it would make the
        // bonus lines no-tracking too, so the run they are assigned to would never be saved.
        var people = await staff.ToListAsync(ct);
        // One read for both: the lines this run may take, and the lines it held before (to release those it no longer takes).
        var read = await db.PaymentVoucherBonusLines
            .Where(b => b.RunId == run.Id
                || (people.Contains(b.EmployeeId) && b.Voucher!.Status == PaymentVoucherStatus.Paid
                    && (b.PayrollYear < year || (b.PayrollYear == year && b.PayrollMonth <= month))))
            .ToListAsync(ct);
        var staffSet = people.ToHashSet();
        // A line held by this run came from a paid voucher (a paid voucher is never cancelled): only person and month decide.
        var candidates = read.Where(b => staffSet.Contains(b.EmployeeId)
            && (b.PayrollYear < year || (b.PayrollYear == year && b.PayrollMonth <= month))).ToList();
        var holderIds = candidates.Where(b => b.RunId is Guid r && r != run.Id).Select(b => b.RunId!.Value).Distinct().ToList();
        var binding = holderIds.Count == 0 ? [] : (await db.PayrollRuns.AsNoTracking()
            .Where(r => holderIds.Contains(r.Id)
                && (r.Status == PayrollRunStatus.Locked || r.Status == PayrollRunStatus.Approved || r.Status == PayrollRunStatus.Paid))
            .Select(r => r.Id).ToListAsync(ct)).ToHashSet();

        var mine = candidates.Where(b => b.RunId is not Guid holder || holder == run.Id || !binding.Contains(holder)).ToList();
        foreach (var line in mine) line.RunId = run.Id;
        var taken = mine.Select(b => b.Id).ToHashSet();
        foreach (var released in read.Where(b => b.RunId == run.Id && !taken.Contains(b.Id)))
            released.RunId = null;
        return mine;
    }

    /// <summary>A reversed run gives its bonus lines back: the next run of their month or later takes them.</summary>
    private async Task ReleaseVoucherBonusesAsync(PayrollRun run, CancellationToken ct)
    {
        foreach (var line in await db.PaymentVoucherBonusLines.Where(b => b.RunId == run.Id).ToListAsync(ct))
            line.RunId = null;
    }

    /// <summary>
    /// What another run has already allocated against these people's advances and not yet taken,
    /// per employee.
    ///
    /// <para>
    /// Only <c>Locked</c> runs count. An approved run's recovery is already in
    /// <c>WageAdvance.Recovered</c>; a reversed one gave its back; a draft one may hold lines from
    /// a previous compute but has to be computed again before it can be approved, so its figures
    /// bind nothing.
    /// </para>
    /// </summary>
    /// <param name="computedBefore">
    /// When set, only runs computed strictly before that instant — the prefix this run skipped
    /// when it computed. A run computed *after* this one took the tail and cannot be standing in
    /// front of it, whichever of the two is approved first.
    /// </param>
    private async Task<Dictionary<Guid, decimal>> ClaimedElsewhereAsync(
        Guid runId, IQueryable<Guid> staff, DateTime? computedBefore, CancellationToken ct)
    {
        var rows = await (
            from line in db.PayrollRunLines.AsNoTracking()
            join other in db.PayrollRuns.AsNoTracking() on line.RunId equals other.Id
            where other.Id != runId && other.Status == PayrollRunStatus.Locked
                  && staff.Contains(line.EmployeeId)
            select new { line.EmployeeId, line.AdvanceRecovered, other.ComputedAtUtc }).ToListAsync(ct);

        // Filtered and summed in memory: SQLite stores decimal as TEXT, so a server-side Sum would
        // be adding strings.
        return rows
            .Where(r => computedBefore is not DateTime cut || (r.ComputedAtUtc is DateTime at && at < cut))
            .GroupBy(r => r.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Sum(r => r.AdvanceRecovered));
    }

    /// <summary>
    /// What is really left on one person's advances, after the prefix another live run has already
    /// allocated to itself.
    ///
    /// <para>
    /// The engine allocates greedily in the recorded order, so whatever another run claimed came
    /// off the oldest balances. Consuming that much before the list is built is the same statement
    /// read from this run's side, and it is what keeps one 5 000 advance from being deducted twice.
    /// </para>
    /// </summary>
    private static List<Engine.OpenAdvance>? Outstanding(List<WageAdvance>? advances, decimal claimedElsewhere)
    {
        if (advances is null) return null;

        var open = new List<Engine.OpenAdvance>();
        var skip = Math.Max(0m, claimedElsewhere);

        foreach (var advance in advances)
        {
            var outstanding = advance.Amount - advance.Recovered;
            if (outstanding <= 0m) continue;

            var stepped = Math.Min(outstanding, skip);
            skip -= stepped;
            outstanding -= stepped;

            if (outstanding > 0m) open.Add(new Engine.OpenAdvance(advance.Id, outstanding));
        }

        return open;
    }

    // ---------- unlock ----------

    /// <summary>Back to Draft, so the attendance sheet for the period can be edited again.</summary>
    public async Task<PayrollResult<PayrollRun>> UnlockAsync(Guid runId, string? by, CancellationToken ct = default)
    {
        await using var scope = await db.BeginWriteScopeAsync(ct);

        var run = await db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return PayrollResult<PayrollRun>.NotFound("Payroll run not found.");
        if (run.Status != PayrollRunStatus.Locked)
            return PayrollResult<PayrollRun>.Conflict("Only a locked run can be unlocked.");

        // LockedBy is left where it is: it says who locked the month, and a recompute overwrites
        // it. Clearing it here would lose the only record of who closed the sheet in the first
        // place, which is the question asked when a figure is queried.
        run.Status = PayrollRunStatus.Draft;
        // Once reopened, this is a draft again and takes the current payroll policy when it is
        // recomputed. Its earlier locked figures remain on the historical document until then.
        run.CalculateStatutoryDeductions = false;
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);

        return PayrollResult<PayrollRun>.Success(run);
    }

    // ---------- approve ----------

    /// <summary>
    /// The month is agreed: the ledger takes it, and the advances it recovered are closed.
    ///
    /// <para>
    /// <b>One entry per branch per run</b>, never one per employee — nine hundred entries a month
    /// would bury every other document in the journal and say nothing the run's own lines do not
    /// already say. What an inspector needs from the journal is the wage bill, the debt to the
    /// fund, the tax withheld and what is owed to staff; what they need per person is the payslip.
    /// </para>
    /// </summary>
    public async Task<PayrollResult<PayrollRun>> ApproveAsync(Guid runId, string? by, CancellationToken ct = default)
    {
        await using var scope = await db.BeginWriteScopeAsync(ct);

        var run = await db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return PayrollResult<PayrollRun>.NotFound("Payroll run not found.");
        if (run.Status == PayrollRunStatus.Draft)
            return PayrollResult<PayrollRun>.Conflict("Compute the run before approving it.");
        if (run.Status != PayrollRunStatus.Locked)
            return PayrollResult<PayrollRun>.Conflict("Only a locked run can be approved.");

        // ⚠ An adjustment typed after the figures were computed is on no payslip and in no entry.
        // Approving around it strands it for good: the run leaves Locked, so it can no longer be
        // edited, and only reversing the whole month would bring it back — while the run detail
        // goes on listing a bonus the paper denies.
        if (await AdjustmentsMovedAsync(run, ct))
            return PayrollResult<PayrollRun>.Conflict(
                $"{run.Number} carries adjustments that are not in its figures; compute it again before approving.",
                PayrollProblems.NotRecomputed, new Dictionary<string, string> { ["run"] = run.Number });

        // ⚠ A raise typed after the compute is on no payslip and in no entry. Approving around it
        // locks the month on the old salary until reversed.
        if (await PayMovedAsync(run, ct))
            return PayrollResult<PayrollRun>.Conflict(
                $"An employee's base pay has changed since {run.Number} was computed; compute it again before approving.",
                PayrollProblems.PayMoved, new Dictionary<string, string> { ["run"] = run.Number });

        // Planned before anything is posted. If the balances have moved under the run — an advance
        // recovered by another month, one recorded since — the accrual would credit 425 with money
        // it never cleared, and nothing downstream would ever say so.
        var (takes, moved) = await PlanAdvancesAsync(run, ct);
        if (moved)
            return PayrollResult<PayrollRun>.Conflict(
                $"The advances {run.Number} recovered have changed since it was computed; compute it again before approving.",
                PayrollProblems.AdvancesMoved, new Dictionary<string, string> { ["run"] = run.Number });

        var accrual = await AccrualAsync(run, ct);
        if (PayrollPosting.Accrual(run.Id, run.BranchId, run.Number, accrual, by, PeriodEndUtc(run))
            is PostingDraft draft)
        {
            // PostAsync is idempotent on (sourceType, sourceId): approving twice — a double click,
            // a retried request — posts once and the second call simply finds the entry. A run for a month whose books
            // are closed — a corrected run after a reversal — is booked on the first open day (COR-E01, 2026-11-05).
            var entry = await ledger.PostAsync(await BookClosings.OntoOpenBooksAsync(db, draft, ct), ct);
            run.LedgerEntryId = entry?.Id ?? await db.LedgerEntries.AsNoTracking()
                .Where(e => e.SourceType == LedgerSources.PayrollRun && e.SourceId == run.Id && !e.IsReversal)
                .Select(e => (Guid?)e.Id)
                .FirstOrDefaultAsync(ct);
        }

        CloseAdvances(takes, run);

        run.Status = PayrollRunStatus.Approved;
        run.ApprovedAtUtc = DateTime.UtcNow;
        run.ApprovedBy = Trim(by, 200);

        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);

        return PayrollResult<PayrollRun>.Success(run);
    }

    // ---------- reverse ----------

    /// <summary>
    /// Undoes an approved run: the entry is mirrored, the advances reopen, and the month can be
    /// opened and computed again because a reversed run no longer counts as the live one.
    ///
    /// <para>
    /// A run that has been paid against is not reversible. The money left the box; a reversal
    /// would say it never did, and the branch's cash would be out by a payday with nothing in the
    /// books to explain it. Reverse the payment first, or correct the figures in the next run.
    /// </para>
    /// </summary>
    public async Task<PayrollResult<PayrollRun>> ReverseRunAsync(
        Guid runId, string reason, string? by, CancellationToken ct = default)
    {
        var why = (reason ?? "").Trim();
        if (why.Length == 0) return PayrollResult<PayrollRun>.Invalid("A reversal has to say why.");

        await using var scope = await db.BeginWriteScopeAsync(ct);

        var run = await db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return PayrollResult<PayrollRun>.NotFound("Payroll run not found.");
        if (run.Status == PayrollRunStatus.Paid)
            return PayrollResult<PayrollRun>.Conflict(
                "A paid run is final; correct it with an adjustment in the next run.");
        if (run.Status != PayrollRunStatus.Approved)
            return PayrollResult<PayrollRun>.Conflict("Only an approved run can be reversed.");

        if (await PaidByAsync(run.Id, ct) is string payment)
            return PayrollResult<PayrollRun>.Conflict(
                $"Payment {payment} has already paid part of this run. Reverse that payment first.",
                PayrollProblems.RunPaid, new Dictionary<string, string> { ["payment"] = payment });

        if (run.LedgerEntryId is Guid entryId)
        {
            var entry = await db.LedgerEntries.Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == entryId, ct);
            if (entry is not null)
            {
                var reversal = await ledger.ReverseAsync(entry, why, by, ct);
                run.ReversalEntryId = reversal.Id;
            }
        }

        await ReopenAdvancesAsync(run, ct);
        await ReleaseVoucherBonusesAsync(run, ct);

        // Both links stay on the row. That is the whole point of keeping a reversed run: what was
        // posted, and what undid it.
        run.Status = PayrollRunStatus.Reversed;
        run.ReversedAtUtc = DateTime.UtcNow;
        run.ReversedBy = Trim(by, 200);
        run.ReversalReason = Trim(why, 400);

        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);

        return PayrollResult<PayrollRun>.Success(run);
    }

    // ---------- adjustments ----------

    /// <summary>
    /// A one-off figure for one person in this run, replaced in place. An amount of zero removes
    /// it, which is how a bonus typed by mistake is taken back without a second route.
    /// </summary>
    public async Task<PayrollResult<PayrollAdjustment?>> UpsertAdjustmentAsync(
        Guid runId, Guid employeeId, UpsertPayrollAdjustmentRequest r, string? by, CancellationToken ct = default)
    {
        var key = (r?.Key ?? "").Trim().ToLowerInvariant();
        if (key.Length is 0 or > 30) return PayrollResult<PayrollAdjustment?>.Invalid("An adjustment needs a key.");
        var reason = (r!.Reason ?? "").Trim();
        if (reason.Length == 0 && r.Amount != 0m)
            return PayrollResult<PayrollAdjustment?>.Invalid("An adjustment has to say why.");

        await using var scope = await db.BeginWriteScopeAsync(ct);

        var run = await db.PayrollRuns.FirstOrDefaultAsync(x => x.Id == runId, ct);
        if (run is null) return PayrollResult<PayrollAdjustment?>.NotFound("Payroll run not found.");
        if (run.Status is not (PayrollRunStatus.Draft or PayrollRunStatus.Locked))
            return PayrollResult<PayrollAdjustment?>.Conflict(
                "Adjustments belong to a run that has not been approved yet.");
        // On this run, or of its branch: an adjustment for someone the run does not pay means
        // nothing, and the route's branch check covers the run, not the person (P0.1). A line
        // keeps someone who moved branch since the compute; the branch admits one not yet computed.
        var employeeBranch = await db.Employees.Where(e => e.Id == employeeId)
            .Select(e => (Guid?)e.BranchId).FirstOrDefaultAsync(ct);
        if (employeeBranch is null)
            return PayrollResult<PayrollAdjustment?>.NotFound("Employee not found.");
        if (employeeBranch != run.BranchId
            && !await db.PayrollRunLines.AnyAsync(l => l.RunId == runId && l.EmployeeId == employeeId, ct))
            return PayrollResult<PayrollAdjustment?>.NotFound("That employee is not on this run.");

        var existing = await db.PayrollAdjustments
            .FirstOrDefaultAsync(a => a.RunId == runId && a.EmployeeId == employeeId && a.Key == key, ct);

        if (r.Amount == 0m)
        {
            if (existing is not null) db.PayrollAdjustments.Remove(existing);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            // Removed, and the figures do not move until the run is computed again — the same
            // rule every other input in this module follows.
            return PayrollResult<PayrollAdjustment?>.Success(null, RecomputeHint(run));
        }

        if (existing is null)
        {
            existing = new PayrollAdjustment
            {
                RunId = runId, EmployeeId = employeeId, Key = key, CreatedBy = Trim(by, 200)
            };
            db.PayrollAdjustments.Add(existing);
        }

        existing.Amount = r.Amount;
        existing.InSocialBase = r.InSocialBase;
        existing.InTaxBase = r.InTaxBase;
        existing.Reason = Trim(reason, 400)!;

        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);

        return PayrollResult<PayrollAdjustment?>.Success(existing, RecomputeHint(run));
    }

    /// <summary>A code the screen says in words, like every other warning this module sends.</summary>
    public const string RecomputeNeeded = "recompute_needed";

    private static string? RecomputeHint(PayrollRun run) =>
        run.Status == PayrollRunStatus.Locked ? RecomputeNeeded : null;

    // ---------- what the ledger is told ----------

    /// <summary>
    /// The run's totals with the one split the engine does not make: deductions that reduce what
    /// the wage costs, and deductions withheld for somebody else.
    ///
    /// <para>
    /// The treatments are read from the run's <b>own settings snapshot</b>, not from the settings
    /// as they stand today. A deduction reclassified in April must not change what March's entry
    /// meant.
    /// </para>
    /// </summary>
    public async Task<PayrollAccrual> AccrualAsync(PayrollRun run, CancellationToken ct = default)
    {
        var deductions = await (
            from element in db.PayrollRunLineElements.AsNoTracking()
            join line in db.PayrollRunLines.AsNoTracking() on element.LineId equals line.Id
            where line.RunId == run.Id && element.Kind == PayrollElementKind.Deduction
            select new { element.Key, element.Amount }).ToListAsync(ct);

        var (wageCost, other) = SplitDeductions(
            deductions.Select(d => (d.Key, d.Amount)), Snapshot(run));

        return new PayrollAccrual(
            run.Gross, wageCost, other,
            run.SocialEmployee, run.SocialEmployer, run.IncomeTax,
            run.EmployerLevies, run.AdvancesRecovered, run.Net, run.PaidByVoucher);
    }

    /// <summary>
    /// Splits deduction elements into the two the books distinguish: what the company saved on
    /// the wage, and what it withheld for somebody else. Both come back positive.
    ///
    /// <para>
    /// Pure and public so the posting tests can make the same split without a database — this is
    /// the one piece of the accrual that is a judgement rather than a sum, and a second copy of
    /// it written in a test would agree with itself and with nothing else.
    /// </para>
    /// </summary>
    public static (decimal WageCost, decimal Other) SplitDeductions(
        IEnumerable<(string Key, decimal Amount)> elements, Engine.PayrollSettings? snapshot)
    {
        var treatments = (snapshot?.Deductions ?? [])
            .ToDictionary(d => d.Key, d => d.Treatment, StringComparer.OrdinalIgnoreCase);

        decimal wageCost = 0m, other = 0m;
        foreach (var (key, signed) in elements)
        {
            // Elements are signed negative; the posting wants positives.
            var amount = -signed;
            // A key the snapshot does not carry can only be a definition deleted after the run was
            // computed. Treated as a wage-cost reduction, which is the entry that needs no
            // placeholder account and no ⚠ — the conservative half of the two.
            if (treatments.GetValueOrDefault(key, Engine.DeductionTreatment.ReducesWageCost)
                == Engine.DeductionTreatment.Other) other += amount;
            else wageCost += amount;
        }

        return (wageCost, other);
    }

    /// <summary>The settings the run was computed against, or null on a run that never computed.</summary>
    public static Engine.PayrollSettings? Snapshot(PayrollRun run)
    {
        if (string.IsNullOrWhiteSpace(run.SettingsSnapshotJson)) return null;
        try { return JsonSerializer.Deserialize<Engine.PayrollSettings>(run.SettingsSnapshotJson, Json); }
        catch (JsonException) { return null; }
    }

    // ---------- advances ----------

    /// <summary>
    /// What this run will take off each advance when it is approved, or the fact that it can no
    /// longer take it.
    ///
    /// <para>
    /// The split is <b>re-derived</b> rather than stored — there is no recovery row — by replaying
    /// the same allocation compute made: the prefix already claimed by the runs computed before
    /// this one is stepped over, then each advance is taken to its outstanding in recorded order
    /// until the line's recovered total is exhausted. Ordering on the advance <i>number</i> rather
    /// than on the granted date is what makes the replay faithful: a number is minted in order and
    /// never moves, so a back-dated advance entered after the compute sorts last and cannot steal
    /// the recovery the payslip already showed against another one.
    /// </para>
    /// <para>
    /// When the replay cannot find what the run says it recovered, nothing is taken and the run is
    /// refused. Approving anyway would credit 425 with money the company never got back.
    /// </para>
    /// </summary>
    private async Task<(List<(WageAdvance Advance, decimal Take)> Takes, bool Moved)> PlanAdvancesAsync(
        PayrollRun run, CancellationToken ct)
    {
        // SQLite keeps decimals as TEXT, so "> 0" in SQL would compare strings. Read, then filter.
        var recovered = (await db.PayrollRunLines.AsNoTracking()
                .Where(l => l.RunId == run.Id)
                .Select(l => new { l.EmployeeId, l.AdvanceRecovered })
                .ToListAsync(ct))
            .Where(l => l.AdvanceRecovered > 0m)
            .GroupBy(l => l.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.AdvanceRecovered));
        if (recovered.Count == 0) return ([], false);

        var employeeIds = recovered.Keys.ToList();
        var open = await db.WageAdvances
            .Where(a => employeeIds.Contains(a.EmployeeId) && a.Status == WageAdvanceStatus.Open)
            .OrderBy(a => a.Number).ThenBy(a => a.Id)
            .ToListAsync(ct);

        var ahead = await ClaimedElsewhereAsync(
            run.Id,
            db.PayrollRunLines.AsNoTracking().Where(l => l.RunId == run.Id).Select(l => l.EmployeeId),
            run.ComputedAtUtc,
            ct);

        var takes = new List<(WageAdvance, decimal)>();
        var byEmployee = open.GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (employeeId, wanted) in recovered)
        {
            var remaining = wanted;
            var skip = Math.Max(0m, ahead.GetValueOrDefault(employeeId));

            foreach (var advance in byEmployee.GetValueOrDefault(employeeId, []))
            {
                var available = advance.Amount - advance.Recovered;
                if (available <= 0m) continue;

                var stepped = Math.Min(available, skip);
                skip -= stepped;
                available -= stepped;
                if (available <= 0m || remaining <= 0m) continue;

                var take = Math.Min(available, remaining);
                takes.Add((advance, take));
                remaining -= take;
            }

            if (remaining > 0m) return ([], true);
        }

        return (takes, false);
    }

    /// <summary>Applies the plan. Nothing here decides anything; the deciding was done above.</summary>
    private static void CloseAdvances(List<(WageAdvance Advance, decimal Take)> takes, PayrollRun run)
    {
        foreach (var (advance, take) in takes)
        {
            advance.Recovered += take;
            // A pointer to the last run that took from it, for the advances screen. Correctness
            // does not hang on it: a reversal reads the run's own lines, because this single stamp
            // is overwritten the moment a later run takes the rest of the same advance.
            advance.RecoveredInRunId = run.Id;
            if (advance.Recovered >= advance.Amount) advance.Status = WageAdvanceStatus.Recovered;
        }
    }

    /// <summary>
    /// Gives back exactly what this run's payslips deducted, newest balance first.
    ///
    /// <para>
    /// Driven by the run's own lines rather than by <c>RecoveredInRunId</c>. That stamp holds one
    /// run, so when two runs part-recover one advance the earlier one's reversal used to find
    /// nothing at all to give back: the payslip was voided and the advance went on reading as
    /// repaid, with the company out the difference and nothing in the books saying so.
    /// </para>
    /// <para>
    /// Unwound from the newest recovered balance backwards, which keeps <c>Recovered</c> a
    /// left-to-right prefix of the ordered advances — the shape every allocation here assumes.
    /// Which of a person's advances carries the restored balance is not a fact about money: they
    /// owe the same total either way, and the next run takes it back in the same order.
    /// </para>
    /// </summary>
    private async Task ReopenAdvancesAsync(PayrollRun run, CancellationToken ct)
    {
        var recovered = (await db.PayrollRunLines.AsNoTracking()
                .Where(l => l.RunId == run.Id)
                .Select(l => new { l.EmployeeId, l.AdvanceRecovered })
                .ToListAsync(ct))
            .Where(l => l.AdvanceRecovered > 0m)
            .GroupBy(l => l.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.AdvanceRecovered));
        if (recovered.Count == 0) return;

        var employeeIds = recovered.Keys.ToList();
        var advances = await db.WageAdvances
            .Where(a => employeeIds.Contains(a.EmployeeId))
            .OrderBy(a => a.Number).ThenBy(a => a.Id)
            .ToListAsync(ct);

        foreach (var group in advances.GroupBy(a => a.EmployeeId))
        {
            var remaining = recovered[group.Key];
            var ordered = group.ToList();

            // The stamp stops pointing at a run that no longer exists as a recovery, whether or
            // not this advance is the one that gives money back.
            foreach (var advance in ordered)
                if (advance.RecoveredInRunId == run.Id) advance.RecoveredInRunId = null;

            for (var i = ordered.Count - 1; i >= 0 && remaining > 0m; i--)
            {
                var advance = ordered[i];
                var give = Math.Min(advance.Recovered, remaining);
                if (give <= 0m) continue;

                advance.Recovered -= give;
                remaining -= give;
                if (advance.Recovered < advance.Amount) advance.Status = WageAdvanceStatus.Open;
            }
        }
    }

    /// <summary>
    /// Whether the run's adjustments say something its computed figures do not.
    ///
    /// <para>
    /// Compared against the run's own lines rather than against a timestamp: an adjustment edited
    /// in place keeps the date it was created on, and the question is never when it was typed but
    /// whether the payslips carry it. Every adjustment the engine took became an element of kind
    /// <c>Adjustment</c> with the same key, rounded amount and base flags, so the two sets either
    /// match or the month has to be computed again.
    /// </para>
    /// <para>
    /// Only people who actually have a line count. An adjustment saved against somebody this run
    /// does not pay is ignored by the compute too, and letting it count here would leave the month
    /// unapprovable with no way to clear it.
    /// </para>
    /// </summary>
    public async Task<bool> AdjustmentsMovedAsync(PayrollRun run, CancellationToken ct = default)
    {
        var regional = regionalSettings.Get();

        var onRun = (await db.PayrollRunLines.AsNoTracking()
            .Where(l => l.RunId == run.Id)
            .Select(l => l.EmployeeId)
            .ToListAsync(ct)).ToHashSet();

        var wanted = (await db.PayrollAdjustments.AsNoTracking()
                .Where(a => a.RunId == run.Id)
                .Select(a => new { a.EmployeeId, a.Key, a.Amount, a.InSocialBase, a.InTaxBase })
                .ToListAsync(ct))
            .Where(a => onRun.Contains(a.EmployeeId))
            // An amount that rounds to nothing is no element at all; the engine drops it.
            .Select(a => (a.EmployeeId, a.Key, regional.RoundMoney(a.Amount), a.InSocialBase, a.InTaxBase))
            .Where(a => a.Item3 != 0m)
            .ToHashSet();

        var carried = (await (
                from element in db.PayrollRunLineElements.AsNoTracking()
                join line in db.PayrollRunLines.AsNoTracking() on element.LineId equals line.Id
                // The voucher bonus is an Adjustment element too, but its source is the voucher, not a typed adjustment.
                where line.RunId == run.Id && element.Kind == PayrollElementKind.Adjustment
                      && element.Key != Engine.PayrollEngine.VoucherBonusKey
                select new { line.EmployeeId, element.Key, element.Amount, element.InSocialBase, element.InTaxBase })
            .ToListAsync(ct))
            .Select(e => (e.EmployeeId, e.Key, e.Amount, e.InSocialBase, e.InTaxBase))
            .ToHashSet();

        return !wanted.SetEquals(carried);
    }

    /// <summary>
    /// Whether any employee's base rate or pay basis has changed since the run was computed.
    ///
    /// <para>
    /// A payslip prints the rate snapshotted at compute time; a raise typed after the compute is on
    /// no payslip until the run is computed again. Only Draft and Locked runs can still be computed,
    /// so only they are compared — an approved month keeps what it was paid on.
    /// </para>
    /// </summary>
    public async Task<bool> PayMovedAsync(PayrollRun run, CancellationToken ct = default)
    {
        if (run.Status is not (PayrollRunStatus.Draft or PayrollRunStatus.Locked))
            return false;

        var rows = await (
            from line in db.PayrollRunLines.AsNoTracking().Where(l => l.RunId == run.Id)
            join employee in db.Employees.AsNoTracking() on line.EmployeeId equals employee.Id
            select new
            {
                LineBaseRate = line.BaseRate,
                LinePayBasis = line.PayBasis,
                EmployeeBaseRate = employee.BaseRate,
                EmployeePayBasis = employee.PayBasis
            }).ToListAsync(ct);

        // Compared in memory: SQLite stores decimal as TEXT, so comparison in SQL would be lexical.
        return rows.Any(r => r.EmployeeBaseRate != r.LineBaseRate || r.EmployeePayBasis != r.LinePayBasis);
    }

    // ---------- shared reads ----------

    /// <summary>
    /// The number of a payment that <b>still stands</b> against this run, if any.
    ///
    /// <para>
    /// A reversed payment is not one. <c>WagePayment</c> deliberately carries no reversal flag —
    /// the reversing ledger entry is the record, which is the same reason the payments list derives
    /// <c>WagePaymentRowDto.IsReversed</c> from the ledger — so the question is asked of the ledger
    /// here too. Asked of the join alone, reversing a payment put the cash back and then bricked
    /// the month: the run could never be reversed, and its branch could never carry another run for
    /// that period.
    /// </para>
    /// <para>
    /// Both halves have to agree. The ledger says the payment was undone; the lines say whether any
    /// of its money is still out. A payment whose entry never posted would otherwise block the run
    /// for good on the strength of a row that gave everything back.
    /// </para>
    /// </summary>
    private async Task<string?> PaidByAsync(Guid runId, CancellationToken ct)
    {
        var paid = await (
            from row in db.WagePaymentLines.AsNoTracking()
            join line in db.PayrollRunLines.AsNoTracking() on row.RunLineId equals line.Id
            join payment in db.WagePayments.AsNoTracking() on row.WagePaymentId equals payment.Id
            where line.RunId == runId
            select new { payment.Number, payment.LedgerEntryId, line.PaidAmount }).ToListAsync(ct);
        if (paid.Count == 0) return null;

        var entryIds = paid.Where(p => p.LedgerEntryId is not null)
            .Select(p => p.LedgerEntryId!.Value).Distinct().ToList();
        HashSet<Guid> reversed = entryIds.Count == 0
            ? []
            : (await db.LedgerEntries.AsNoTracking()
                .Where(e => e.ReversalOfEntryId != null && entryIds.Contains(e.ReversalOfEntryId!.Value))
                .Select(e => e.ReversalOfEntryId!.Value)
                .ToListAsync(ct)).ToHashSet();

        return paid
            .Where(p => p.LedgerEntryId is not Guid entry || !reversed.Contains(entry))
            // Compared in memory: SQLite stores decimal as TEXT, so "> 0" in SQL compares strings.
            .Where(p => p.PaidAmount > 0m)
            .Select(p => p.Number)
            .OrderBy(number => number, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public Task<PayrollRun?> ReadAsync(Guid id, CancellationToken ct = default) =>
        db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);

    /// <summary>
    /// How many lines raised each warning. Read as the stored JSON of every line rather than as a
    /// column, because a warning is a sentence about a payslip and not a fact the run indexes on;
    /// it is a few hundred short strings even for the largest branch.
    /// </summary>
    public async Task<IReadOnlyList<PayrollWarningCountDto>> WarningCountsAsync(
        Guid runId, CancellationToken ct = default)
    {
        var stored = await db.PayrollRunLines.AsNoTracking()
            .Where(l => l.RunId == runId && l.WarningsJson != null)
            .Select(l => l.WarningsJson!)
            .ToListAsync(ct);

        return stored
            .SelectMany(Warnings)
            .GroupBy(code => code, StringComparer.Ordinal)
            .Select(g => new PayrollWarningCountDto(g.Key, g.Count()))
            .OrderByDescending(w => w.Lines).ThenBy(w => w.Code, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The warning codes stored on one line, or none at all when the column is unreadable. The
    /// screen resolves <c>hr.warning.{code}</c>; a code it does not know is shown as itself rather
    /// than dropped.
    /// </summary>
    public static IReadOnlyList<string> Warnings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }

    // ---------- helpers ----------

    /// <summary>
    /// The day a month's wages are a cost: the last day of the period, never the day the button
    /// was pressed. A March run approved on 5 April belongs to March in every report that has a
    /// date on it.
    /// </summary>
    public static DateTime PeriodEndUtc(PayrollRun run) =>
        new(run.PeriodYear, run.PeriodMonth,
            DateTime.DaysInMonth(run.PeriodYear, run.PeriodMonth), 0, 0, 0, DateTimeKind.Utc);

    // ⚠ The third private copy of this in the codebase (ReceivableDocuments, RentalDocuments).
    // The shared DocumentNumbers helper of docs/07 §7 item 19 is still unextracted; see forOthers.
    private static string Numbered(string prefix, int next) => $"{prefix}-{next:D6}";

    private static string? Trim(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
