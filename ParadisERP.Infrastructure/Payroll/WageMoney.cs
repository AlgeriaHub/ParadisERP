using ParadisERP.Infrastructure.Receivables;
using ParadisERP.Application.Contracts;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Ledger;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Payroll;

/// <summary>
/// The money side of payroll: cash drawn against a wage before the month closes, the wages
/// actually handed over against an approved run, and the transfer that clears what was withheld
/// on everybody's behalf (docs/07 §5.8, §5.10).
///
/// <para>
/// <b>Everything here moves real money, so everything here writes both halves.</b> A cash advance
/// with no <see cref="CashMovement"/> leaves the Z-report short with no name against it; wages
/// that do not clear 421 leave the books saying staff were paid while the cash is still in the
/// safe. Each document writes its cash row and its ledger entry inside the one write scope the
/// subscription filter already opened, or it writes neither.
/// </para>
/// <para>
/// <b>Wages are refused while the run is not approved, and the refusal names the status it is
/// in.</b> That is the one hard gate in this file. Everything else that looks like a reason to
/// stop — a box holding less than is being handed out, an employee who has left, a fund paid more
/// than it is owed — comes back as a warning beside a document that was written. The box in
/// particular: a branch refills its safe on payday morning and the recorded count is behind all
/// of it, so a builder who turns that into a refusal blocks the largest regular cash outflow the
/// business has (CLAUDE.md rule one; docs/07 §5.14).
/// </para>
/// <para>
/// <b>What is not here.</b> Recovering an advance belongs to the run — the engine decides how much
/// and <c>PayrollDocuments</c> books it at Approve and gives it back at Reverse. And there is no
/// write-off: <see cref="WageAdvanceStatus.WrittenOff"/> exists as a state, but forgiving an
/// advance means posting it to some expense account, and which one is the accountant's answer
/// (§5.8, Q24). A builder picking one would bury a real cost in whatever line looked closest.
/// </para>
/// </summary>
public sealed class WageMoney(PosDbContext db, LedgerPoster ledger, IRegionalSettingsProvider regionalSettings)
{
    // =========================================================================================
    // Advances — §5.8
    // =========================================================================================

    /// <summary>
    /// Hands cash (or a transfer) to a worker against wages not yet earned, and records the debt
    /// the next run takes back.
    /// </summary>
    public async Task<PayrollResult<WageAdvance>> GrantAdvanceAsync(
        GrantWageAdvanceRequest r, string? by, CancellationToken ct = default)
    {
        var amount = regionalSettings.Get().RoundMoney(r.Amount);
        if (amount <= 0) return PayrollResult<WageAdvance>.Invalid("An advance must be for a positive amount.");

        if (!TryMethod(r.PayoutMethod, PayoutMethod.Cash, out var method))
            return PayrollResult<WageAdvance>.Invalid($"'{r.PayoutMethod}' is not a payout method; use Cash or Bank.");

        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == r.EmployeeId, ct);
        if (employee is null) return PayrollResult<WageAdvance>.NotFound("Employee not found.");

        var (box, boxProblem) = await BoxAsync(method, r.CashBoxId, ct);
        if (boxProblem is not null) return PayrollResult<WageAdvance>.Invalid(boxProblem);

        var advance = new WageAdvance
        {
            EmployeeId = employee.Id,
            // Frozen: the cost belongs to the branch that had them when the money went out,
            // whatever a later transfer says. The paying box may be another branch's, which is
            // exactly what makes the posting inter-branch.
            BranchId = employee.BranchId,
            Amount = amount,
            GrantedAtUtc = r.GrantedAtUtc ?? DateTime.UtcNow,
            PayoutMethod = method,
            CashBoxId = box?.Id,
            Status = WageAdvanceStatus.Open,
            Reference = Trim(r.Reference, 100),
            Note = Trim(r.Note, 400),
            CreatedBy = Trim(by, 200)
        };

        await using var tx = await db.BeginWriteScopeAsync(ct);

        // Inside the scope, like every other numbered document here: the write filter has
        // serialised the writers on the version counter, so a count taken here cannot hand the
        // same number to two advances.
        advance.Number = Numbered("ADV", await db.WageAdvances.CountAsync(ct) + 1);
        db.WageAdvances.Add(advance);

        var warning = box is null
            ? null
            : await TakeCashAsync(
                box, advance.Amount, LedgerSources.WageAdvance, advance.Id, advance.Number, by,
                advance.GrantedAtUtc, ct);

        // Dr 425 at the employee's branch / Cr the paying box's branch cash, or the bank.
        // Dated by the grant, not by now: the cash movement above already carries that date, and
        // two dates for one 20 000 DA is a Z-report and a journal that can never be reconciled.
        if (PayrollPosting.Advance(
                advance.Id, advance.BranchId, advance.Number, advance.Amount, method, box?.BranchId, by,
                advance.GrantedAtUtc)
            is PostingDraft draft)
            await ledger.PostAsync(draft, ct);

        // Every amount that leaves is on a payment voucher (answer 24).
        await LinkedPaymentVouchers.AddPaidAsync(
            db, box?.BranchId ?? advance.BranchId, advance.Amount, method, LinkedPaymentVouchers.Advance,
            employee.FullName, $"Avance {advance.Number} — {employee.FullName}",
            LedgerSources.WageAdvance, advance.Id, by, advance.GrantedAtUtc, ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // Said after the document exists, never instead of it: somebody who has left may still be
        // owed a final settlement, and the person handing the money over is the one who knows.
        // What they cannot know without being told is that no run will ever take it back.
        if (employee.LeftOn is not null || !employee.IsActive)
            warning = Join(warning, $"{employee.FullName} is no longer on the payroll; no run will recover this.");

        return PayrollResult<WageAdvance>.Success(advance, warning);
    }

    public async Task<IReadOnlyList<WageAdvanceDto>> ListAdvancesAsync(
        Guid? employeeId, Guid? branchId, string? status, int take, CancellationToken ct = default)
    {
        var query = db.WageAdvances.AsNoTracking();
        if (employeeId is Guid eid) query = query.Where(a => a.EmployeeId == eid);
        if (branchId is Guid bid) query = query.Where(a => a.BranchId == bid);
        if (Enum.TryParse<WageAdvanceStatus>(status, true, out var wanted))
            query = query.Where(a => a.Status == wanted);

        var rows = await query
            .OrderByDescending(a => a.GrantedAtUtc)
            .Take(Math.Clamp(take, 1, 1000))
            .ToListAsync(ct);

        return await MapAdvancesAsync(rows, ct);
    }

    public async Task<WageAdvanceDto?> ReadAdvanceAsync(Guid id, CancellationToken ct = default)
    {
        var advance = await db.WageAdvances.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        return advance is null ? null : (await MapAdvancesAsync([advance], ct)).Single();
    }

    /// <summary>
    /// Shapes advances for the wire, resolving the person, the branch and the recovering run once
    /// for the whole page rather than once per row.
    /// </summary>
    public async Task<IReadOnlyList<WageAdvanceDto>> MapAdvancesAsync(
        IReadOnlyList<WageAdvance> advances, CancellationToken ct = default)
    {
        if (advances.Count == 0) return [];

        var employeeIds = advances.Select(a => a.EmployeeId).Distinct().ToList();
        var people = (await db.Employees.AsNoTracking()
                .Where(e => employeeIds.Contains(e.Id))
                .Select(e => new { e.Id, e.Number, e.FullName })
                .ToListAsync(ct))
            .ToDictionary(e => e.Id);

        var branches = await BranchNamesAsync(advances.Select(a => a.BranchId), ct);

        var runIds = advances.Where(a => a.RecoveredInRunId is not null)
            .Select(a => a.RecoveredInRunId!.Value).Distinct().ToList();
        var runs = runIds.Count == 0
            ? []
            : await db.PayrollRuns.AsNoTracking()
                .Where(x => runIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.Number, ct);

        // The box's branch, only for the advances that were paid in cash: an inter-branch advance
        // is one whose money left a box belonging to somebody other than the employee's branch.
        var boxIds = advances.Where(a => a.CashBoxId is not null)
            .Select(a => a.CashBoxId!.Value).Distinct().ToList();
        var boxBranches = boxIds.Count == 0
            ? []
            : await db.CashBoxes.AsNoTracking()
                .Where(x => boxIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.BranchId, ct);

        return advances.Select(a =>
        {
            people.TryGetValue(a.EmployeeId, out var person);
            var boxBranch = a.CashBoxId is Guid boxId ? boxBranches.GetValueOrDefault(boxId) : (Guid?)null;

            return new WageAdvanceDto(
                a.Id, a.Number, a.EmployeeId, person?.Number ?? "", person?.FullName ?? "",
                a.BranchId, branches.GetValueOrDefault(a.BranchId) ?? "",
                a.Amount, a.Recovered, a.Amount - a.Recovered,
                a.GrantedAtUtc, a.PayoutMethod.ToString(), a.Status.ToString(),
                a.CashBoxId, a.RecoveredInRunId,
                a.RecoveredInRunId is Guid runId ? runs.GetValueOrDefault(runId) : null,
                a.Reference, a.Note, a.CreatedBy,
                boxBranch is Guid branch && branch != a.BranchId);
        }).ToList();
    }

    // =========================================================================================
    // Wage payments — §5.10
    // =========================================================================================

    /// <summary>
    /// Pays wages against an approved run: one cash movement, one ledger entry, and a line against
    /// each payslip line it settles. Several payments against one run are normal — a branch pays
    /// its cash crew on the 2nd and transfers the salaried staff on the 5th.
    /// </summary>
    public async Task<PayrollResult<WagePayment>> PayAsync(
        Guid runId, PayWagesRequest r, string? by, CancellationToken ct = default)
    {
        if (r.RunId != Guid.Empty && r.RunId != runId)
            return PayrollResult<WagePayment>.Invalid("This payment names a different run from the one it was sent to.");

        if (!TryMethod(r.PayoutMethod, PayoutMethod.Cash, out var method))
            return PayrollResult<WagePayment>.Invalid($"'{r.PayoutMethod}' is not a payout method; use Cash or Bank.");

        var run = await db.PayrollRuns.FirstOrDefaultAsync(x => x.Id == runId, ct);
        if (run is null) return PayrollResult<WagePayment>.NotFound("Payroll run not found.");

        // ⚠ The one hard gate. A Draft or Locked run is a working figure and not a decision, and
        // money paid against one clears a liability the ledger was never told about.
        if (run.Status is not (PayrollRunStatus.Approved or PayrollRunStatus.Paid))
            return PayrollResult<WagePayment>.Conflict(
                $"Wages are paid against an approved run; {run.Number} is {run.Status}.");

        var (box, boxProblem) = await BoxAsync(method, r.CashBoxId, ct);
        if (boxProblem is not null) return PayrollResult<WagePayment>.Invalid(boxProblem);

        var lines = await db.PayrollRunLines.Where(l => l.RunId == runId).ToListAsync(ct);
        var selection = await SelectAsync(run, lines, r, method, ct);
        // The code travels with the sentence: SelectAsync's refusals are the ones a cashier meets.
        if (!selection.Ok)
            return PayrollResult<WagePayment>.Invalid(selection.Message!, selection.Code, selection.Args);

        var taken = selection.Value!;
        if (taken.Count == 0)
            return PayrollResult<WagePayment>.Conflict(
                lines.Any(l => l.Net - l.PaidAmount > 0)
                    ? $"Nobody on {run.Number} is still owed wages by {method}."
                    : $"Every payslip on {run.Number} is already settled.");

        var payment = new WagePayment
        {
            RunId = run.Id,
            // The branch whose wage debt is being settled, which need not be the branch whose box
            // paid — that difference is what makes the entry inter-branch.
            BranchId = run.BranchId,
            PaidAtUtc = r.PaidAtUtc ?? DateTime.UtcNow,
            PayoutMethod = method,
            CashBoxId = box?.Id,
            Amount = regionalSettings.Get().RoundMoney(taken.Sum(x => x.Amount)),
            Reference = Trim(r.Reference, 100),
            Note = Trim(r.Note, 400),
            CreatedBy = Trim(by, 200)
        };

        await using var tx = await db.BeginWriteScopeAsync(ct);

        payment.Number = Numbered("WP", await db.WagePayments.CountAsync(ct) + 1);
        db.WagePayments.Add(payment);

        foreach (var (line, amount) in taken)
        {
            payment.Lines.Add(new WagePaymentLine
            {
                WagePaymentId = payment.Id,
                RunLineId = line.Id,
                Amount = amount
            });

            line.PaidAmount += amount;
            // Only when this one payment closed the line. A part-paid crew has a line the next
            // payment finishes, and pointing it at the first would name the wrong document.
            if (line.PaidAmount >= line.Net) line.WagePaymentId = payment.Id;
        }

        var warning = box is null
            ? null
            : await TakeCashAsync(
                box, payment.Amount, LedgerSources.WagePayment, payment.Id, payment.Number, by,
                payment.PaidAtUtc, ct);

        if (PayrollPosting.Payment(
                payment.Id, payment.BranchId, payment.Number, payment.Amount, method, box?.BranchId, by,
                payment.PaidAtUtc)
            is PostingDraft draft)
            payment.LedgerEntryId = (await ledger.PostAsync(draft, ct))?.Id;

        // Salaries too go out on a payment voucher (answer 24): one for this payment, naming the run.
        await LinkedPaymentVouchers.AddPaidAsync(
            db, box?.BranchId ?? payment.BranchId, payment.Amount, method, LinkedPaymentVouchers.Wages,
            null, $"Salaires {run.Number} — paiement {payment.Number}",
            LedgerSources.WagePayment, payment.Id, by, payment.PaidAtUtc, ct);

        // Paid is a fact about the lines rather than a step somebody takes: the run is paid when
        // no line has anything left on it.
        if (lines.All(l => l.PaidAmount >= l.Net))
        {
            run.Status = PayrollRunStatus.Paid;
            run.PaidAtUtc = payment.PaidAtUtc;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return PayrollResult<WagePayment>.Success(payment, warning);
    }

    /// <summary>
    /// Undoes a payment. It is never edited — the worker is holding a numbered document — so this
    /// reverses the entry, puts the cash back as its own counted movement, and returns the lines
    /// it settled to unpaid.
    /// </summary>
    public async Task<PayrollResult<WagePayment>> ReversePaymentAsync(
        Guid paymentId, string? reason, string? by, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return PayrollResult<WagePayment>.Invalid(
                "A reversal needs a reason; wages taken back with nothing said cannot be explained to the person they were paid to.");

        var payment = await db.WagePayments.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        if (payment is null) return PayrollResult<WagePayment>.NotFound("Wage payment not found.");

        // Asked of the ledger rather than of a flag on the payment, because the entry is the
        // record. Reversing twice would double the correction and look exactly as legitimate as
        // the first one in every report.
        if (payment.LedgerEntryId is Guid entryId &&
            await db.LedgerEntries.AnyAsync(e => e.ReversalOfEntryId == entryId, ct))
            return PayrollResult<WagePayment>.Conflict($"{payment.Number} has already been reversed.");

        var run = await db.PayrollRuns.FirstOrDefaultAsync(x => x.Id == payment.RunId, ct);
        var lineIds = payment.Lines.Select(l => l.RunLineId).ToList();
        var lines = await db.PayrollRunLines.Where(l => lineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);

        await using var tx = await db.BeginWriteScopeAsync(ct);

        foreach (var paid in payment.Lines)
        {
            if (!lines.TryGetValue(paid.RunLineId, out var line)) continue;
            line.PaidAmount = Math.Max(0m, line.PaidAmount - paid.Amount);
            if (line.WagePaymentId == payment.Id) line.WagePaymentId = null;
        }

        string? warning = null;
        if (payment.CashBoxId is Guid boxId)
        {
            var box = await db.CashBoxes.FirstOrDefaultAsync(x => x.Id == boxId, ct);
            if (box is null)
                warning = "The box this was paid from no longer exists, so the cash was not put back.";
            else
                // Kind Adjustment rather than a mirrored Disbursement: the drawer is being
                // corrected, and a counted correction is a different event from the payout it
                // undoes (§5.10).
                await AddCashAsync(box, payment.Amount, LedgerSources.WagePayment, payment.Id,
                    payment.Number, by, reason, ct);
        }

        if (payment.LedgerEntryId is Guid id)
        {
            var original = await db.LedgerEntries.Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == id, ct);
            if (original is not null) await ledger.ReverseAsync(original, reason!, by, ct);
        }

        // The money is back, so the voucher that recorded it going out no longer stands.
        await LinkedPaymentVouchers.CancelForAsync(db, LedgerSources.WagePayment, payment.Id, reason, ct);

        // Back to Approved: the money is no longer out, so the wages are owed again. The run keeps
        // its accrual — that is unaffected by who has and has not been handed cash.
        if (run is { Status: PayrollRunStatus.Paid })
        {
            run.Status = PayrollRunStatus.Approved;
            run.PaidAtUtc = null;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return PayrollResult<WagePayment>.Success(payment, warning);
    }

    /// <summary>Every payment against a run, or one payment on its own, with its reversal said in words.</summary>
    public async Task<IReadOnlyList<WagePaymentRowDto>> ListPaymentsAsync(
        Guid? runId, Guid? paymentId, int take, CancellationToken ct = default)
    {
        var query = db.WagePayments.AsNoTracking().Include(p => p.Lines).AsQueryable();
        if (runId is Guid rid) query = query.Where(p => p.RunId == rid);
        if (paymentId is Guid pid) query = query.Where(p => p.Id == pid);

        var payments = await query
            .OrderByDescending(p => p.PaidAtUtc)
            .Take(Math.Clamp(take, 1, 1000))
            .ToListAsync(ct);
        if (payments.Count == 0) return [];

        var runIds = payments.Select(p => p.RunId).Distinct().ToList();
        var runs = await db.PayrollRuns.AsNoTracking()
            .Where(x => runIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Number, ct);

        var branches = await BranchNamesAsync(payments.Select(p => p.BranchId), ct);

        var lineIds = payments.SelectMany(p => p.Lines).Select(l => l.RunLineId).Distinct().ToList();
        var runLines = (await db.PayrollRunLines.AsNoTracking()
                .Where(l => lineIds.Contains(l.Id))
                .Select(l => new { l.Id, l.EmployeeId, l.EmployeeNameSnapshot })
                .ToListAsync(ct))
            .ToDictionary(l => l.Id);

        // One query for the whole page's reversals rather than one per payment: a branch's payday
        // list is read on every open of the screen.
        var entryIds = payments.Where(p => p.LedgerEntryId is not null)
            .Select(p => p.LedgerEntryId!.Value).Distinct().ToList();
        var reversals = entryIds.Count == 0
            ? []
            : (await db.LedgerEntries.AsNoTracking()
                    .Where(e => e.ReversalOfEntryId != null && entryIds.Contains(e.ReversalOfEntryId!.Value))
                    .Select(e => new { Of = e.ReversalOfEntryId!.Value, e.EntryDate, e.Description })
                    .ToListAsync(ct))
                .GroupBy(e => e.Of)
                .ToDictionary(g => g.Key, g => g.First());

        return payments.Select(p =>
        {
            var reversal = p.LedgerEntryId is Guid entry ? reversals.GetValueOrDefault(entry) : null;

            var dto = new WagePaymentDto(
                p.Id, p.Number, p.RunId, runs.GetValueOrDefault(p.RunId) ?? "",
                p.BranchId, branches.GetValueOrDefault(p.BranchId) ?? "",
                p.PaidAtUtc, p.PayoutMethod.ToString(), p.Amount,
                p.CashBoxId, p.Reference, p.Note, p.CreatedBy, p.LedgerEntryId,
                p.Lines.Select(l =>
                {
                    runLines.TryGetValue(l.RunLineId, out var line);
                    return new WagePaymentLineDto(
                        l.RunLineId, line?.EmployeeId ?? Guid.Empty, line?.EmployeeNameSnapshot ?? "", l.Amount);
                }).ToList());

            return new WagePaymentRowDto(dto, reversal is not null, reversal?.EntryDate, reversal?.Description);
        }).ToList();
    }

    // =========================================================================================
    // Statutory payments — §5.9, §5.14
    // =========================================================================================

    /// <summary>
    /// The transfer that clears what was withheld on everybody's behalf. Without it 431, 442 and
    /// 447 accumulate from every run and the balance sheet reads as debt that was settled months
    /// ago (§5.14).
    /// </summary>
    public async Task<PayrollResult<PayrollStatutoryPayment>> PayStatutoryAsync(
        RecordStatutoryPaymentRequest r, string? by, CancellationToken ct = default)
    {
        var code = (r.AccountCode ?? "").Trim();
        if (!PayrollPosting.StatutoryLiabilities.Contains(code, StringComparer.OrdinalIgnoreCase))
            return PayrollResult<PayrollStatutoryPayment>.Invalid(
                $"'{r.AccountCode}' is not a payroll liability; a statutory payment settles " +
                $"{string.Join(", ", PayrollPosting.StatutoryLiabilities)}.");

        var amount = regionalSettings.Get().RoundMoney(r.Amount);
        if (amount <= 0)
            return PayrollResult<PayrollStatutoryPayment>.Invalid(
                "A statutory payment must be for a positive amount.");

        if (!TryMethod(r.PayoutMethod, PayoutMethod.Bank, out var method))
            return PayrollResult<PayrollStatutoryPayment>.Invalid(
                $"'{r.PayoutMethod}' is not a payout method; use Cash or Bank.");

        // Both or neither. The period is optional because one transfer often clears several
        // months (§5.4) — but a year with no month is a period nobody can file against.
        if ((r.PeriodYear is null) != (r.PeriodMonth is null))
            return PayrollResult<PayrollStatutoryPayment>.Invalid(
                "A period is a year and a month together, or neither.");
        if (r.PeriodMonth is int month && month is < 1 or > 12)
            return PayrollResult<PayrollStatutoryPayment>.Invalid("A period is a calendar month, 1 to 12.");

        if (!await db.Branches.AnyAsync(b => b.Id == r.BranchId, ct))
            return PayrollResult<PayrollStatutoryPayment>.NotFound("Branch not found.");

        var (box, boxProblem) = await BoxAsync(method, r.CashBoxId, ct);
        if (boxProblem is not null) return PayrollResult<PayrollStatutoryPayment>.Invalid(boxProblem);

        var payment = new PayrollStatutoryPayment
        {
            BranchId = r.BranchId,
            AccountCode = code,
            Amount = amount,
            PaidAtUtc = r.PaidAtUtc ?? DateTime.UtcNow,
            PayoutMethod = method,
            CashBoxId = box?.Id,
            PeriodYear = r.PeriodYear,
            PeriodMonth = r.PeriodMonth,
            Reference = Trim(r.Reference, 100),
            Note = Trim(r.Note, 400),
            CreatedBy = Trim(by, 200)
        };

        // Read before the posting, so the comparison below is against what the runs accrued and
        // not against a balance this very payment has already moved.
        var standing = await LiabilityAsync(code, r.BranchId, ct);

        await using var tx = await db.BeginWriteScopeAsync(ct);

        db.PayrollStatutoryPayments.Add(payment);

        var memo = payment.Reference ?? code;
        var warning = box is null
            ? null
            : await TakeCashAsync(
                box, payment.Amount, LedgerSources.PayrollStatutoryPayment, payment.Id, memo, by,
                payment.PaidAtUtc, ct);

        if (PayrollPosting.StatutoryPayment(
                payment.Id, payment.BranchId, code, memo, payment.Amount, method, box?.BranchId, by,
                payment.PaidAtUtc)
            is PostingDraft draft)
            payment.LedgerEntryId = (await ledger.PostAsync(draft, ct))?.Id;

        // A statutory payment had no number of its own; its voucher gives it one (answer 24).
        var period = payment.PeriodYear is int y && payment.PeriodMonth is int m ? $" {m:D2}/{y}" : "";
        await LinkedPaymentVouchers.AddPaidAsync(
            db, box?.BranchId ?? payment.BranchId, payment.Amount, method, LinkedPaymentVouchers.Statutory,
            payment.Reference, $"Cotisations et impôts {code}{period}",
            LedgerSources.PayrollStatutoryPayment, payment.Id, by, payment.PaidAtUtc, ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        // A warning, never a refusal: a fund is routinely paid a round figure, an arrears
        // settlement, or a month whose run has not been approved yet.
        if (amount > standing.Outstanding)
            warning = Join(warning,
                $"Paying {amount:0.##} against account {code}, which stands at {standing.Outstanding:0.##}.");

        return PayrollResult<PayrollStatutoryPayment>.Success(payment, warning);
    }

    public async Task<IReadOnlyList<PayrollStatutoryPaymentDto>> ListStatutoryPaymentsAsync(
        Guid? branchId, string? accountCode, int? year, int take, CancellationToken ct = default)
    {
        var query = db.PayrollStatutoryPayments.AsNoTracking();
        if (branchId is Guid bid) query = query.Where(p => p.BranchId == bid);
        if (!string.IsNullOrWhiteSpace(accountCode)) query = query.Where(p => p.AccountCode == accountCode);
        if (year is int y) query = query.Where(p => p.PeriodYear == y);

        var rows = await query
            .OrderByDescending(p => p.PaidAtUtc)
            .Take(Math.Clamp(take, 1, 1000))
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var branches = await BranchNamesAsync(rows.Select(p => p.BranchId), ct);
        var accounts = await AccountNamesAsync(ct);

        return rows.Select(p => new PayrollStatutoryPaymentDto(
            p.Id, p.BranchId, branches.GetValueOrDefault(p.BranchId) ?? "",
            p.AccountCode, accounts.GetValueOrDefault(p.AccountCode) ?? "",
            p.Amount, p.PaidAtUtc, p.PayoutMethod.ToString(),
            p.CashBoxId, p.PeriodYear, p.PeriodMonth,
            p.Reference, p.Note, p.CreatedBy, p.LedgerEntryId)).ToList();
    }

    /// <summary>All three liabilities at once — what the accountant's screen opens on.</summary>
    public async Task<IReadOnlyList<PayrollLiabilityDto>> LiabilitiesAsync(
        Guid? branchId, CancellationToken ct = default)
    {
        var rows = new List<PayrollLiabilityDto>(PayrollPosting.StatutoryLiabilities.Count);
        foreach (var code in PayrollPosting.StatutoryLiabilities)
            rows.Add(await LiabilityAsync(code, branchId, ct));
        return rows;
    }

    /// <summary>
    /// What one payroll liability holds, read from the ledger itself rather than from a column
    /// that could drift from it.
    /// </summary>
    /// <param name="branchId">Null for the company as a whole.</param>
    public async Task<PayrollLiabilityDto> LiabilityAsync(
        string accountCode, Guid? branchId, CancellationToken ct = default)
    {
        var account = await db.LedgerAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Code == accountCode, ct);
        // No such account means nothing has ever posted to it. A company upgrading into this
        // release before the seeder has run reads zero rather than throwing at the screen.
        if (account is null) return new PayrollLiabilityDto(accountCode, "", branchId, 0m, 0m, 0m);

        var query = db.LedgerEntryLines.AsNoTracking().Where(l => l.AccountId == account.Id);
        if (branchId is Guid id) query = query.Where(l => l.BranchId == id);

        // Summed in memory: SQLite stores decimal as TEXT and a server-side Sum compares strings.
        var rows = await query.Select(l => new { l.Debit, l.Credit }).ToListAsync(ct);
        var accrued = rows.Sum(l => l.Credit);
        var paid = rows.Sum(l => l.Debit);
        return new PayrollLiabilityDto(accountCode, account.NameFr, branchId, accrued, paid, accrued - paid);
    }

    // =========================================================================================
    // Shared
    // =========================================================================================

    /// <summary>
    /// Which payslip lines this payment takes, and how much of each.
    /// </summary>
    /// <remarks>
    /// Named lines are the general case; an empty list is the "pay everyone" button, and it takes
    /// the people this method actually pays — the crew paid in cash and the staff paid by transfer
    /// are two different paydays, and one button must not hand a bank-paid salary over the
    /// counter. Overpaying is refused and the refusal names the figure, because the alternative —
    /// silently clamping — leaves the cashier believing they handed over more than the books say.
    /// </remarks>
    private async Task<PayrollResult<List<(PayrollRunLine Line, decimal Amount)>>> SelectAsync(
        PayrollRun run, List<PayrollRunLine> lines, PayWagesRequest r, PayoutMethod method, CancellationToken ct)
    {
        var taken = new List<(PayrollRunLine, decimal)>();

        if (r.Lines is { Count: > 0 })
        {
            var byId = lines.ToDictionary(l => l.Id);
            var seen = new HashSet<Guid>();

            foreach (var requested in r.Lines)
            {
                if (!byId.TryGetValue(requested.RunLineId, out var line))
                    return PayrollResult<List<(PayrollRunLine, decimal)>>.Invalid(
                        $"Payslip line {requested.RunLineId} is not on {run.Number}.");
                // One line per payslip line per payment, the code rule the index deliberately does
                // not enforce: two rows for the same person in one payment would each pass the
                // overpay check on their own and together hand over twice the net.
                if (!seen.Add(line.Id))
                    return PayrollResult<List<(PayrollRunLine, decimal)>>.Invalid(
                        $"{line.EmployeeNameSnapshot} appears twice in one payment; pay them once.");
                if (requested.Amount <= 0)
                    return PayrollResult<List<(PayrollRunLine, decimal)>>.Invalid(
                        $"{line.EmployeeNameSnapshot}: a payment line must be for a positive amount.");

                var remaining = line.Net - line.PaidAmount;
                if (requested.Amount > remaining)
                    return PayrollResult<List<(PayrollRunLine, decimal)>>.Invalid(
                        $"{line.EmployeeNameSnapshot} would be overpaid by {requested.Amount - remaining:0.##}: " +
                        $"{remaining:0.##} of {line.Net:0.##} is still owed.",
                        // A code beside the sentence: this refusal is met by a cashier on payday
                        // and every figure in it is a number, so the screen rebuilds it in the
                        // reader's own language rather than printing English over a French form.
                        PayrollProblems.Overpaid,
                        new Dictionary<string, string>
                        {
                            ["name"] = line.EmployeeNameSnapshot,
                            ["over"] = $"{requested.Amount - remaining:0.##}",
                            ["owed"] = $"{remaining:0.##}",
                            ["net"] = $"{line.Net:0.##}"
                        });

                taken.Add((line, requested.Amount));
            }

            return PayrollResult<List<(PayrollRunLine, decimal)>>.Success(taken);
        }

        // Filtered in memory — SQLite stores decimal as TEXT, so `PaidAmount < Net` in SQL would
        // compare strings.
        var candidates = lines.Where(l => l.Net - l.PaidAmount > 0).ToList();
        if (candidates.Count > 0)
        {
            var ids = candidates.Select(l => l.EmployeeId).Distinct().ToList();
            var byEmployee = (await db.Employees.AsNoTracking()
                    .Where(e => ids.Contains(e.Id))
                    .Select(e => new { e.Id, e.PayoutMethod })
                    .ToListAsync(ct))
                .ToDictionary(e => e.Id, e => e.PayoutMethod);

            // An employee the directory no longer has is paid by whatever this payment is: the
            // wage is owed either way, and a payslip that cannot be paid at all is worse than one
            // paid by the wrong method.
            candidates = candidates
                .Where(l => byEmployee.GetValueOrDefault(l.EmployeeId, method) == method)
                .ToList();
        }

        foreach (var line in candidates) taken.Add((line, line.Net - line.PaidAmount));
        return PayrollResult<List<(PayrollRunLine, decimal)>>.Success(taken);
    }

    /// <summary>The box a cash document leaves, or the sentence saying why there isn't one.</summary>
    private async Task<(CashBox? Box, string? Problem)> BoxAsync(
        PayoutMethod method, Guid? cashBoxId, CancellationToken ct)
    {
        // A bank transfer moves no drawer, so it writes no cash movement at all — the receipt
        // voucher's rule, and the reason a transfer can never appear in a Z-report.
        if (method != PayoutMethod.Cash) return (null, null);
        if (cashBoxId is not Guid id) return (null, "Cash goes out of a box; name which one.");

        var box = await db.CashBoxes.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (box is null) return (null, "Cash box not found.");
        if (!box.IsActive) return (null, $"The box '{box.NameFr}' is closed.");
        return (box, null);
    }

    /// <summary>Money out of a box, and the warning when the count says there is less in it than that.</summary>
    private async Task<string?> TakeCashAsync(
        CashBox box, decimal amount, string sourceType, Guid sourceId, string? reference, string? by,
        DateTime at, CancellationToken ct)
    {
        var balance = await BalanceOfAsync(box.Id, ct);

        db.CashMovements.Add(new CashMovement
        {
            CashBoxId = box.Id,
            Direction = CashDirection.Out,
            Amount = amount,
            BalanceAfter = balance - amount,
            Kind = CashMovementKind.Disbursement,
            SourceType = sourceType,
            SourceId = sourceId,
            UserName = Trim(by, 200),
            Reference = Trim(reference, 100),
            OccurredAtUtc = at
        });

        // ⚠ Never a refusal — the remittance precedent. The safe is refilled on payday morning and
        // the recorded balance is behind all of it.
        return amount > balance
            ? $"Paying {amount:0.##} out of '{box.NameFr}', which the count puts at {balance:0.##}."
            : null;
    }

    /// <summary>Money back into a box, counted as an adjustment because that is what a correction is.</summary>
    private async Task AddCashAsync(
        CashBox box, decimal amount, string sourceType, Guid sourceId, string? reference, string? by,
        string? note, CancellationToken ct)
    {
        var balance = await BalanceOfAsync(box.Id, ct);

        db.CashMovements.Add(new CashMovement
        {
            CashBoxId = box.Id,
            Direction = CashDirection.In,
            Amount = amount,
            BalanceAfter = balance + amount,
            Kind = CashMovementKind.Adjustment,
            SourceType = sourceType,
            SourceId = sourceId,
            UserName = Trim(by, 200),
            Reference = Trim(reference, 100),
            Note = Trim(note, 400)
        });
    }

    // Read under the version lock, so a till's cash sale landing at the same moment and this
    // movement each record the balance the other left.
    private Task<decimal> BalanceOfAsync(Guid cashBoxId, CancellationToken ct) =>
        CashBoxBalance.ForWriteAsync(db, cashBoxId, ct);

    private async Task<Dictionary<Guid, string>> BranchNamesAsync(
        IEnumerable<Guid> branchIds, CancellationToken ct)
    {
        var ids = branchIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        return await db.Branches.AsNoTracking()
            .Where(b => ids.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.NameFr, ct);
    }

    private async Task<Dictionary<string, string>> AccountNamesAsync(CancellationToken ct) =>
        await db.LedgerAccounts.AsNoTracking()
            .Where(a => PayrollPosting.StatutoryLiabilities.Contains(a.Code))
            .ToDictionaryAsync(a => a.Code, a => a.NameFr, ct);

    /// <summary>
    /// A method name off the wire. Blank takes the document's own default rather than failing — an
    /// advance is cash unless somebody says otherwise, a statutory payment is a transfer.
    /// </summary>
    private static bool TryMethod(string? value, PayoutMethod fallback, out PayoutMethod method)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            method = fallback;
            return true;
        }

        return Enum.TryParse(value, true, out method);
    }

    private static string Numbered(string prefix, int next) => $"{prefix}-{next:D6}";

    private static string? Trim(string? value, int max) =>
        value is { Length: > 0 } && value.Length > max ? value[..max] : value;

    private static string Join(string? first, string second) =>
        string.IsNullOrEmpty(first) ? second : $"{first} {second}";
}
