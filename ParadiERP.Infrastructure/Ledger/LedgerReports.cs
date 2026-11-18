using ParadiERP.Application.Contracts;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Ledger;

/// <summary>
/// The accounting reports, read from the ledger.
///
/// <para>
/// Every one of them is an indexed filter on period, branch and account over two tables — not
/// a pass over sales, receiving notes and remittances totalling each in its own way. Built the
/// other way a product report and an accounting report disagree and nobody can say which is
/// right, and at the transaction rate this business runs at they would not finish either
/// (design §5.10).
/// </para>
/// <para>
/// Aggregates run client-side after <c>ToListAsync</c>, like every other total in this
/// codebase: SQLite stores decimal as TEXT, so a SUM pushed to the database there would be
/// comparing and adding strings. The projection is four columns wide to keep that cheap.
/// </para>
/// </summary>
public sealed class LedgerReports(PosDbContext db)
{
    /// <summary>The payroll settlements a cash box of another branch may pay; never dropped from the group's figures (P0.2).</summary>
    private static readonly string[] PayrollSettlements =
        [LedgerSources.WagePayment, LedgerSources.WageAdvance, LedgerSources.PayrollStatutoryPayment];

    /// <summary>
    /// A full fiscal year plus a margin. Every list endpoint in this API caps what one call
    /// will load; a report over an unbounded range is the same problem with a nicer name.
    /// </summary>
    public const int MaxPeriodDays = 400;

    private sealed record Row(Guid AccountId, Guid BranchId, decimal Debit, decimal Credit);

    /// <summary>
    /// Reads the lines in a window.
    ///
    /// <para>
    /// <paramref name="branchId"/> null means the group. That is the moment inter-branch
    /// entries are dropped: a remittance or a transfer is real at each branch and is nothing
    /// at all for the company as a whole. Asked for one branch they stay, because that branch
    /// genuinely lost the cash or gained the goods.
    /// </para>
    /// <para>
    /// Dropping the whole entry is safe only because every entry balances — both halves of an
    /// inter-branch movement live in the same entry and therefore net to zero for the group.
    /// </para>
    /// </summary>
    private async Task<List<Row>> ReadAsync(
        DateTime? from, DateTime to, Guid? branchId, bool includeInterBranch, CancellationToken ct)
    {
        var query =
            from line in db.LedgerEntryLines.AsNoTracking()
            join entry in db.LedgerEntries.AsNoTracking() on line.EntryId equals entry.Id
            where entry.EntryDate < to
            select new { line, entry };

        if (from is not null) query = query.Where(x => x.entry.EntryDate >= from);
        if (branchId is not null) query = query.Where(x => x.line.BranchId == branchId);
        // P0.2: payroll paid out of another branch's box was once marked inter-branch, which dropped a real settlement from
        // the group's figures. Those entries (and their reversals) are kept whatever their mark, so past group reports
        // read right without any posted row being edited.
        if (!includeInterBranch)
            query = query.Where(x => !x.entry.IsInterBranch || PayrollSettlements.Contains(x.entry.SourceType));

        return await query
            .Select(x => new Row(x.line.AccountId, x.line.BranchId, x.line.Debit, x.line.Credit))
            .ToListAsync(ct);
    }

    private Task<Dictionary<Guid, LedgerAccount>> AccountsAsync(CancellationToken ct) =>
        db.LedgerAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, ct);

    private Task<Dictionary<Guid, string>> BranchNamesAsync(CancellationToken ct) =>
        db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.NameFr, ct);

    /// <summary>Debit minus credit; the natural-side balance is applied per account type later.</summary>
    private static decimal Signed(IEnumerable<Row> rows) => rows.Sum(r => r.Debit - r.Credit);

    /// <summary>
    /// The amount an account normally carries: assets and expenses grow with debits, everything
    /// else with credits. Reporting a liability as a negative number is technically true and
    /// practically unreadable.
    /// </summary>
    private static decimal Natural(LedgerAccountType type, decimal signed) =>
        type is LedgerAccountType.Asset or LedgerAccountType.Expense ? signed : -signed;

    // ---------- trial balance ----------

    public async Task<TrialBalanceDto> TrialBalanceAsync(
        DateTime from, DateTime to, Guid? branchId, CancellationToken ct)
    {
        var accounts = await AccountsAsync(ct);
        var rows = await ReadAsync(from, to, branchId, includeInterBranch: branchId is not null, ct);

        var lines = rows
            .GroupBy(r => r.AccountId)
            .Select(g =>
            {
                var account = accounts[g.Key];
                var debit = g.Sum(r => r.Debit);
                var credit = g.Sum(r => r.Credit);
                return new TrialBalanceRowDto(
                    g.Key, account.Code, account.NameAr, account.NameFr, account.NameEn,
                    account.Type.ToString(), debit, credit, Natural(account.Type, debit - credit));
            })
            .OrderBy(r => r.Code, StringComparer.Ordinal)
            .ToList();

        var totalDebit = lines.Sum(r => r.Debit);
        var totalCredit = lines.Sum(r => r.Credit);

        return new TrialBalanceDto(
            from, to, branchId, branchId is null, lines, totalDebit, totalCredit,
            // Always true if nothing is broken. Reported rather than assumed, because the one
            // number worth showing on this screen is whether the books add up.
            Math.Round(totalDebit - totalCredit, 3) == 0);
    }

    // ---------- profit and loss ----------

    public async Task<ProfitAndLossDto> ProfitAndLossAsync(
        DateTime from, DateTime to, Guid? branchId, CancellationToken ct)
    {
        var accounts = await AccountsAsync(ct);
        var branchNames = await BranchNamesAsync(ct);
        var consolidated = branchId is null;

        var rows = await ReadAsync(from, to, branchId, includeInterBranch: !consolidated, ct);

        bool IsIncome(Guid id) => accounts[id].Type == LedgerAccountType.Income;
        bool IsExpense(Guid id) => accounts[id].Type == LedgerAccountType.Expense;

        List<ProfitAndLossLineDto> Group(Func<Guid, bool> keep) => rows
            .Where(r => keep(r.AccountId))
            .GroupBy(r => r.AccountId)
            .Select(g => new ProfitAndLossLineDto(
                accounts[g.Key].Code, accounts[g.Key].NameAr, accounts[g.Key].NameFr, accounts[g.Key].NameEn,
                Natural(accounts[g.Key].Type, Signed(g))))
            .Where(l => l.Amount != 0)
            .OrderBy(l => l.Code, StringComparer.Ordinal)
            .ToList();

        var income = Group(IsIncome);
        var expenses = Group(IsExpense);
        var totalIncome = income.Sum(l => l.Amount);
        var totalExpenses = expenses.Sum(l => l.Amount);

        var byBranch = rows
            .Where(r => IsIncome(r.AccountId) || IsExpense(r.AccountId))
            .GroupBy(r => r.BranchId)
            .Select(g => new BranchResultDto(
                g.Key,
                branchNames.GetValueOrDefault(g.Key, "—"),
                Natural(LedgerAccountType.Income, Signed(g.Where(r => IsIncome(r.AccountId)))),
                Natural(LedgerAccountType.Expense, Signed(g.Where(r => IsExpense(r.AccountId)))),
                0m))
            .Select(b => b with { NetProfit = b.Income - b.Expenses })
            .OrderByDescending(b => b.NetProfit)
            .ToList();

        // What consolidation removed. Zero today and expected to stay zero: nothing the
        // system posts between branches touches profit, by design — a remittance is an asset
        // moving and a transfer is goods moving. It is reported rather than assumed because
        // the day a module does post an inter-branch charge, this is the figure that says so
        // instead of the group quietly earning money from itself.
        var eliminated = 0m;
        if (consolidated)
        {
            var withInterBranch = await ReadAsync(from, to, null, includeInterBranch: true, ct);
            var grossProfit = withInterBranch
                .Where(r => IsIncome(r.AccountId) || IsExpense(r.AccountId))
                .Sum(r => IsIncome(r.AccountId) ? r.Credit - r.Debit : -(r.Debit - r.Credit));
            eliminated = Math.Round(grossProfit - (totalIncome - totalExpenses), 3);
        }

        return new ProfitAndLossDto(
            from, to, branchId, consolidated, income, expenses,
            totalIncome, totalExpenses, totalIncome - totalExpenses, byBranch, eliminated);
    }

    // ---------- balance sheet ----------

    public async Task<BalanceSheetDto> BalanceSheetAsync(DateTime asOf, Guid? branchId, CancellationToken ct)
    {
        var accounts = await AccountsAsync(ct);
        var consolidated = branchId is null;
        // No opening date: a balance sheet is a position, so it reads everything up to the
        // date. There is no period-balance rollup yet — see the report notes in docs.
        var rows = await ReadAsync(null, asOf, branchId, includeInterBranch: !consolidated, ct);

        List<BalanceSheetLineDto> Group(LedgerAccountType type) => rows
            .Where(r => accounts[r.AccountId].Type == type)
            .GroupBy(r => r.AccountId)
            .Select(g => new BalanceSheetLineDto(
                accounts[g.Key].Code, accounts[g.Key].NameAr, accounts[g.Key].NameFr, accounts[g.Key].NameEn,
                Natural(type, Signed(g))))
            .Where(l => l.Amount != 0)
            .OrderBy(l => l.Code, StringComparer.Ordinal)
            .ToList();

        var assets = Group(LedgerAccountType.Asset);
        var liabilities = Group(LedgerAccountType.Liability);
        IReadOnlyList<BalanceSheetLineDto> equity = Group(LedgerAccountType.Equity);

        // The result for the period is computed, not posted: there is no year-end close in
        // this system, so income and expenses are rolled into equity at read time instead of
        // being moved by an entry nobody would have raised.
        var netResult =
            Natural(LedgerAccountType.Income, Signed(rows.Where(r => accounts[r.AccountId].Type == LedgerAccountType.Income))) -
            Natural(LedgerAccountType.Expense, Signed(rows.Where(r => accounts[r.AccountId].Type == LedgerAccountType.Expense)));

        var totalAssets = assets.Sum(l => l.Amount);
        var totalLiabilities = liabilities.Sum(l => l.Amount);
        var totalEquity = equity.Sum(l => l.Amount);

        // One branch's sheet does not balance on its own, and should not be expected to: a
        // remittance takes cash out of Oran and puts it in the safe at Alger, and Oran has to
        // carry the claim on the group somewhere. That somewhere is the inter-branch current
        // account, and it is derived rather than posted because the two sides always cancel
        // across the group — an account that is only ever zero in total is not worth writing a
        // row for on every transfer.
        if (!consolidated)
        {
            var residual = Math.Round(totalAssets - (totalLiabilities + totalEquity + netResult), 3);
            if (residual != 0)
            {
                var liaison = accounts.Values.FirstOrDefault(a => a.Code == LedgerAccounts.InterBranchCurrentAccount);
                equity = [.. equity, new BalanceSheetLineDto(
                    LedgerAccounts.InterBranchCurrentAccount,
                    liaison?.NameAr ?? "", liaison?.NameFr ?? "Compte de liaison inter-unités",
                    liaison?.NameEn ?? "Inter-branch current account", residual)];
                totalEquity += residual;
            }
        }

        return new BalanceSheetDto(
            asOf, branchId, consolidated, assets, liabilities, equity,
            totalAssets, totalLiabilities, totalEquity, netResult,
            Math.Round(totalAssets - (totalLiabilities + totalEquity + netResult), 3) == 0);
    }

    // ---------- account statement ----------

    /// <summary>
    /// One account's movements with a running balance, each row carrying the document that
    /// produced it. This is the drill-down: a figure in any report above leads here, and a row
    /// here leads to the sale, receiving note or remittance behind it.
    /// </summary>
    public async Task<AccountStatementDto?> AccountStatementAsync(
        Guid accountId, DateTime from, DateTime to, Guid? branchId, int take, CancellationToken ct)
    {
        var account = await db.LedgerAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null) return null;

        // `from` and `to` are also query-expression keywords, so the bounds get local names
        // the compiler will not mistake for the start of a clause.
        var start = from;
        var end = to;

        var before =
            from line in db.LedgerEntryLines.AsNoTracking()
            join entry in db.LedgerEntries.AsNoTracking() on line.EntryId equals entry.Id
            where line.AccountId == accountId && entry.EntryDate < start
            select new { line.Debit, line.Credit, line.BranchId };
        if (branchId is not null) before = before.Where(x => x.BranchId == branchId);
        var opening = Natural(account.Type, (await before.ToListAsync(ct)).Sum(x => x.Debit - x.Credit));

        var window =
            from line in db.LedgerEntryLines.AsNoTracking()
            join entry in db.LedgerEntries.AsNoTracking() on line.EntryId equals entry.Id
            where line.AccountId == accountId && entry.EntryDate >= start && entry.EntryDate < end
            select new
            {
                entry.Id, entry.EntryDate, line.BranchId, entry.SourceType, entry.SourceId,
                entry.Description, entry.IsReversal, line.Debit, line.Credit
            };
        if (branchId is not null) window = window.Where(x => x.BranchId == branchId);

        var movements = await window
            .OrderBy(x => x.EntryDate).ThenBy(x => x.Id)
            .Take(Math.Clamp(take, 1, 2000))
            .ToListAsync(ct);

        var running = opening;
        var statement = new List<AccountStatementRowDto>(movements.Count);
        foreach (var m in movements)
        {
            running += Natural(account.Type, m.Debit - m.Credit);
            statement.Add(new AccountStatementRowDto(
                m.Id, m.EntryDate, m.BranchId, m.SourceType, m.SourceId, m.Description,
                m.IsReversal, m.Debit, m.Credit, running));
        }

        return new AccountStatementDto(
            account.Id, account.Code, account.NameAr, account.NameFr, account.NameEn,
            account.Type.ToString(), from, to, branchId, opening, running, statement);
    }
}
