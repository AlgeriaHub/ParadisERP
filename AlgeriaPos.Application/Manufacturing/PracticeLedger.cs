using AlgeriaPos.Domain;

namespace AlgeriaPos.Application.Manufacturing;

/// <summary>Which stock an issued material leaves (costing plan §4.3): raw material, supplies or a semi-finished product.</summary>
public enum PracticeMaterialGroup { Raw = 0, Supplies = 1, SemiFinished = 2 }

/// <summary>An issue (MI-) or return (MR-) document of the month: its value per stock group, rounded per document and group.</summary>
public sealed record PracticeMaterialDoc(string Number, DateOnly Day, bool IsReturn, IReadOnlyList<PracticeMaterialValue> Values);

public sealed record PracticeMaterialValue(PracticeMaterialGroup Group, decimal Value);

/// <summary>
/// An order's cost to date at one moment, at three decimals: the material documents before it (issues less returns,
/// each document rounded), labour, overhead and expenses as the cost engine values them, and the releases made before
/// it at the values the ledger gives them. <see cref="Total"/> less <see cref="Released"/> is the work in progress.
/// </summary>
public sealed record PracticeCostPoint(decimal Materials, decimal Labour, decimal Overhead, decimal Expenses, decimal Released, decimal Recovery = 0m)
{
    public static PracticeCostPoint Zero { get; } = new(0m, 0m, 0m, 0m, 0m);
    /// <summary>What the order cost, less the scrap it kept (C-09), which is valued only once it closed.</summary>
    public decimal Total => Materials + Labour + Overhead + Expenses - Recovery;
    public decimal WorkInProgress => Total - Released;
}

/// <summary>A release (OR-) of the month at its value.</summary>
public sealed record PracticeRelease(string Number, DateOnly Day, decimal Value);

/// <summary>
/// One order as the month saw it. <see cref="Start"/> and <see cref="End"/> are its cost at the month's start and end,
/// both stopped at the moment the order closed (completed or cancelled). <see cref="LateFrom"/> and <see cref="LateTo"/>,
/// for an order closed before the month's end, bound what it cost after it closed, within the month.
/// </summary>
public sealed record PracticeOrder(
    Guid OrderId, string Number, Guid BranchId,
    bool OpenAtStart, bool OpenAtEnd, DateOnly? ClosedDay,
    PracticeCostPoint Start, PracticeCostPoint End,
    IReadOnlyList<PracticeMaterialDoc> Documents, IReadOnlyList<PracticeRelease> Releases,
    PracticeCostPoint? LateFrom = null, PracticeCostPoint? LateTo = null);

/// <summary>The admin's choices the ledger follows (Phase C1), as saved.</summary>
public sealed record PracticeLedgerChoices(
    ManufacturingAccountingModel Model, MonthEndWorkInProgress MonthEnd, LateCostTreatment LateCosts, VarianceTreatment Variances,
    bool ScrapRecovered = false);

public sealed record PracticeLine(string Slot, decimal Debit, decimal Credit);

public sealed record PracticeEntry(
    string Kind, DateOnly Day, Guid OrderId, string OrderNumber, Guid BranchId, string? Document, IReadOnlyList<PracticeLine> Lines);

public sealed record PracticeLateCost(Guid OrderId, string OrderNumber, string Element, decimal Amount);

/// <summary>An integrity check of the month: what the events say against what the entries carry.</summary>
public sealed record PracticeCheck(string Code, bool Passed, decimal Expected, decimal Actual);

public sealed record PracticeLedgerResult(
    IReadOnlyList<PracticeEntry> Entries, IReadOnlyList<PracticeLateCost> LateCosts, IReadOnlyList<PracticeCheck> Checks,
    IReadOnlyList<string> Notes);

/// <summary>
/// The practice ledger (Phase C2, costing plan §4 and R8): the journal entries the chosen accounting model would make
/// for one month of manufacturing, computed from the events and never written anywhere — not to <c>LedgerEntries</c>,
/// not to a table of its own. Pure: the same orders and choices give the same entries, to the last decimal.
///
/// <para>
/// <b>Charges by nature</b> (C-03, suggested): an issue is charged to consumption (a semi-finished product to production
/// stocked) at its value; labour, overhead and expenses stay where payroll and the vouchers charge them; a release enters
/// finished goods against production stocked; each open order's work in progress is carried at the month's end and
/// reversed on the 1st (C-15: every month, or December only). <b>Perpetual work in progress</b>: every cost enters work
/// in progress as it happens — labour, overhead and expenses through the absorbed accounts — and leaves it at release;
/// what a closed order leaves behind goes to the variance account.
/// </para>
///
/// <para>
/// Costs an order gathers after it closed (C-10) are listed. With the perpetual model and a variance account chosen they
/// go straight to it from the absorbed accounts; otherwise they stay in their charges. Releases are valued at actual cost,
/// the last one carrying the remainder, so planned-against-actual variances (C-13) stay management figures.
/// </para>
/// </summary>
public static class PracticeLedger
{
    public const string KindIssue = "material_issue";
    public const string KindReturn = "material_return";
    public const string KindRelease = "release";
    public const string KindLabour = "labour_absorbed";
    public const string KindOverhead = "overhead_absorbed";
    public const string KindExpenses = "expenses_absorbed";
    public const string KindMonthEndWip = "month_end_wip";
    public const string KindWipReversal = "wip_reversal";
    public const string KindCloseResidual = "close_residual";
    public const string KindLateCost = "late_cost";
    public const string KindScrapKept = "scrap_kept";

    public const string ElementLabour = "labour";
    public const string ElementOverhead = "overhead";
    public const string ElementExpenses = "expenses";

    public const string CheckBalanced = "entries_balance";
    public const string CheckMaterials = "materials_tie";
    public const string CheckReleases = "releases_tie";
    public const string CheckWorkInProgress = "wip_tie";

    /// <summary>Nothing is posted: a practice month for the accountant to review (always said).</summary>
    public const string NotePracticeOnly = "practice_only";
    /// <summary>The month is not over: its end is today.</summary>
    public const string NoteMonthToDate = "month_to_date";
    /// <summary>An open order's releases are valued as its cost sheet values them today, and can move until it closes.</summary>
    public const string NoteOpenReleasesLive = "open_releases_live";
    /// <summary>The admin chose no recovery value for rejects kept as scrap (C-09): no scrap entry.</summary>
    public const string NoteScrapNotRecorded = "scrap_recovery_not_recorded";
    /// <summary>Sales of finished goods are costed once the valuation switch is signed (Phase D).</summary>
    public const string NoteSalesNotIncluded = "finished_goods_sales_not_included";
    /// <summary>A variance account for late costs needs the perpetual model: by nature they are already in their charges.</summary>
    public const string NoteLateCostsByNature = "late_costs_stay_in_charges_by_nature";
    /// <summary>Releases at actual cost leave no planned-against-actual variance to post.</summary>
    public const string NoteVariancesAtActual = "variances_none_at_actual_cost";

    public static PracticeLedgerResult Build(
        PracticeLedgerChoices choices, int year, int month, IReadOnlyList<PracticeOrder> orders, bool monthToDate = false)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(orders);
        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var byNature = choices.Model == ManufacturingAccountingModel.ByNature;
        var entries = new List<PracticeEntry>();
        var late = new List<PracticeLateCost>();
        var carryAtEnd = byNature && (choices.MonthEnd == MonthEndWorkInProgress.Monthly || month == 12);
        var reverseAtStart = byNature && (choices.MonthEnd == MonthEndWorkInProgress.Monthly || month == 1);
        decimal wipExpected = 0m;

        foreach (var o in orders.OrderBy(o => o.Number, StringComparer.Ordinal))
        {
            void Add(string kind, DateOnly day, string? document, params PracticeLine[] lines)
            {
                var kept = lines.Where(l => l.Debit != 0m || l.Credit != 0m).ToList();
                if (kept.Count > 0) entries.Add(new PracticeEntry(kind, day, o.OrderId, o.Number, o.BranchId, document, kept));
            }

            // The work in progress carried from last month comes back on the 1st (by nature).
            if (reverseAtStart && o.OpenAtStart)
            {
                Add(KindWipReversal, first, null, Pair(ManufacturingAccountSlots.ProductionStocked, ManufacturingAccountSlots.WorkInProgress, o.Start.WorkInProgress));
                wipExpected -= o.Start.WorkInProgress;
            }

            foreach (var doc in o.Documents.OrderBy(d => d.Day).ThenBy(d => d.Number, StringComparer.Ordinal))
            {
                var lines = new List<PracticeLine>();
                foreach (var v in doc.Values)
                {
                    var (charge, stock) = Accounts(v.Group, byNature);
                    lines.AddRange(Pair(charge, stock, doc.IsReturn ? -v.Value : v.Value));
                }
                Add(doc.IsReturn ? KindReturn : KindIssue, doc.Day, doc.Number, [.. lines]);
            }

            var day = o.ClosedDay ?? last;
            if (!byNature)
            {
                // Perpetual work in progress: the month's conversion enters it, element by element, until the order closed.
                Add(KindLabour, day, null, Pair(ManufacturingAccountSlots.WorkInProgress, ManufacturingAccountSlots.LabourAbsorbed, o.End.Labour - o.Start.Labour));
                Add(KindOverhead, day, null, Pair(ManufacturingAccountSlots.WorkInProgress, ManufacturingAccountSlots.OverheadAbsorbed, o.End.Overhead - o.Start.Overhead));
                Add(KindExpenses, day, null, Pair(ManufacturingAccountSlots.WorkInProgress, ManufacturingAccountSlots.ExpensesAbsorbed, o.End.Expenses - o.Start.Expenses));
            }

            var releaseCredit = byNature ? ManufacturingAccountSlots.ProductionStocked : ManufacturingAccountSlots.WorkInProgress;
            foreach (var r in o.Releases.OrderBy(r => r.Day).ThenBy(r => r.Number, StringComparer.Ordinal))
                Add(KindRelease, r.Day, r.Number, Pair(ManufacturingAccountSlots.FinishedGoodsStock, releaseCredit, r.Value));

            // Rejects kept as scrap enter scrap stock at their recovery value when the order closes (C-09).
            if (o.ClosedDay is not null)
                Add(KindScrapKept, day, null, Pair(ManufacturingAccountSlots.ScrapStock, releaseCredit, o.End.Recovery));

            if (byNature)
            {
                if (carryAtEnd && o.OpenAtEnd)
                {
                    Add(KindMonthEndWip, last, null, Pair(ManufacturingAccountSlots.WorkInProgress, ManufacturingAccountSlots.ProductionStocked, o.End.WorkInProgress));
                    wipExpected += o.End.WorkInProgress;
                }
            }
            else
            {
                // What a closed order leaves in work in progress (no release, or the remainder below zero) goes to the variance account.
                if (o.ClosedDay is not null)
                    Add(KindCloseResidual, day, null, Pair(ManufacturingAccountSlots.Variance, ManufacturingAccountSlots.WorkInProgress, o.End.WorkInProgress));
                wipExpected += (o.OpenAtEnd ? o.End.WorkInProgress : 0m) - (o.OpenAtStart ? o.Start.WorkInProgress : 0m);
            }

            if (o.LateFrom is { } from && o.LateTo is { } to)
            {
                (string Element, string Absorbed, decimal Amount)[] parts =
                [
                    (ElementLabour, ManufacturingAccountSlots.LabourAbsorbed, to.Labour - from.Labour),
                    (ElementOverhead, ManufacturingAccountSlots.OverheadAbsorbed, to.Overhead - from.Overhead),
                    (ElementExpenses, ManufacturingAccountSlots.ExpensesAbsorbed, to.Expenses - from.Expenses),
                ];
                foreach (var (element, absorbed, amount) in parts.Where(p => p.Amount != 0m))
                {
                    late.Add(new PracticeLateCost(o.OrderId, o.Number, element, amount));
                    if (!byNature && choices.LateCosts == LateCostTreatment.VarianceAccount)
                        Add(KindLateCost, last, null, Pair(ManufacturingAccountSlots.Variance, absorbed, amount));
                }
            }
        }

        // ---------- checks: the events against the entries (R8) ----------

        var all = entries.SelectMany(e => e.Lines).ToList();
        decimal Net(string slot) => all.Where(l => l.Slot == slot).Sum(l => l.Debit - l.Credit);
        var stockSlots = new[] { ManufacturingAccountSlots.RawMaterialStock, ManufacturingAccountSlots.SuppliesStock, ManufacturingAccountSlots.SemiFinishedStock };
        var materialsExpected = orders.SelectMany(o => o.Documents).Sum(d => (d.IsReturn ? -1m : 1m) * d.Values.Sum(v => v.Value));
        var releasesExpected = orders.SelectMany(o => o.Releases).Sum(r => r.Value);
        var unbalanced = entries.Count(e => e.Lines.Sum(l => l.Debit) != e.Lines.Sum(l => l.Credit));
        var checks = new List<PracticeCheck>
        {
            new(CheckBalanced, unbalanced == 0, 0m, unbalanced),
            Check(CheckMaterials, materialsExpected, -stockSlots.Sum(Net)),
            Check(CheckReleases, releasesExpected, Net(ManufacturingAccountSlots.FinishedGoodsStock)),
            Check(CheckWorkInProgress, wipExpected, Net(ManufacturingAccountSlots.WorkInProgress)),
        };

        var notes = new List<string> { NotePracticeOnly };
        if (monthToDate) notes.Add(NoteMonthToDate);
        if (orders.Any(o => o.OpenAtEnd && o.Releases.Count > 0)) notes.Add(NoteOpenReleasesLive);
        if (!choices.ScrapRecovered) notes.Add(NoteScrapNotRecorded);
        notes.Add(NoteSalesNotIncluded);
        if (byNature && choices.LateCosts == LateCostTreatment.VarianceAccount) notes.Add(NoteLateCostsByNature);
        if (choices.Variances == VarianceTreatment.VarianceAccount) notes.Add(NoteVariancesAtActual);

        return new PracticeLedgerResult(entries, late, checks, notes);
    }

    /// <summary>The account an issue is charged to and the stock it leaves, by model (§4.3).</summary>
    private static (string Charge, string Stock) Accounts(PracticeMaterialGroup group, bool byNature) => group switch
    {
        PracticeMaterialGroup.Supplies => (byNature ? ManufacturingAccountSlots.SuppliesConsumption : ManufacturingAccountSlots.WorkInProgress,
            ManufacturingAccountSlots.SuppliesStock),
        PracticeMaterialGroup.SemiFinished => (byNature ? ManufacturingAccountSlots.ProductionStocked : ManufacturingAccountSlots.WorkInProgress,
            ManufacturingAccountSlots.SemiFinishedStock),
        _ => (byNature ? ManufacturingAccountSlots.RawMaterialConsumption : ManufacturingAccountSlots.WorkInProgress,
            ManufacturingAccountSlots.RawMaterialStock),
    };

    /// <summary>Which stock group a material's role leaves.</summary>
    public static PracticeMaterialGroup GroupOf(ManufacturingRole role) => role switch
    {
        ManufacturingRole.Consumable or ManufacturingRole.Accessory or ManufacturingRole.SparePart => PracticeMaterialGroup.Supplies,
        ManufacturingRole.SemiFinished => PracticeMaterialGroup.SemiFinished,
        _ => PracticeMaterialGroup.Raw,
    };

    /// <summary>Debit one slot and credit the other; a negative amount turns the entry round, a zero makes none.</summary>
    private static PracticeLine[] Pair(string debit, string credit, decimal amount) => amount switch
    {
        > 0m => [new PracticeLine(debit, amount, 0m), new PracticeLine(credit, 0m, amount)],
        < 0m => [new PracticeLine(credit, -amount, 0m), new PracticeLine(debit, 0m, -amount)],
        _ => [],
    };

    private static PracticeCheck Check(string code, decimal expected, decimal actual) => new(code, expected == actual, expected, actual);
}
