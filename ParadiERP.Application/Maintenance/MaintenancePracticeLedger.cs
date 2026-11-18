using ParadiERP.Application.Manufacturing;
using ParadiERP.Domain;

namespace ParadiERP.Application.Maintenance;

/// <summary>Which stock a part leaves (plan §5.2): the stock slot of its role, or the chart's stock account for plain merchandise.</summary>
public enum MaintenanceStockGroup { Merchandise = 0, RawMaterial = 1, Supplies = 2, SemiFinished = 3, Finished = 4 }

/// <summary>One stock group's value on a parts document, rounded once per document and group.</summary>
public sealed record MaintenancePracticeValue(MaintenanceStockGroup Group, decimal Value);

/// <summary>An issue (MJI-) or return (MJR-) document of the month that the practice ledger carries (not one posted at issue).</summary>
public sealed record MaintenancePracticeDoc(string Number, DateOnly Day, bool IsReturn, IReadOnlyList<MaintenancePracticeValue> Values);

/// <summary>
/// One job as the month saw it. <see cref="Customer"/>: an outside customer's job (M5), whose parts and service cost go to their
/// own slots. <see cref="LabourMoved"/> and <see cref="OverheadMoved"/> are the month's increase in the job's labour and overhead
/// as its cost values them (MS-33 "moved into jobs"; 0 otherwise). <see cref="CostAtStart"/> and <see cref="CostAtEnd"/> are a
/// customer job's cost to date at the month's start and end, for its work in progress (MS-36), when it was open then.
/// </summary>
public sealed record MaintenancePracticeJob(
    Guid JobId, string Number, Guid BranchId, bool Customer,
    IReadOnlyList<MaintenancePracticeDoc> Documents,
    decimal LabourMoved, decimal OverheadMoved, DateOnly? ClosedDay,
    bool OpenAtStart, bool OpenAtEnd, decimal CostAtStart, decimal CostAtEnd);

/// <summary>A customer job's invoice of the month (plan §5.1): posted by the invoice; listed for the tie-out only.</summary>
public sealed record MaintenancePracticeInvoice(string Number, DateOnly Day, Guid? JobId, string? JobNumber, bool JobMatched, decimal NetAmount);

/// <summary>One credit line of a capitalised repair, frozen at its completion (Phase M7d): a slot (or <c>account:</c> code) and the amount.</summary>
public sealed record MaintenanceCapitalisedCredit(string Slot, decimal Amount);

/// <summary>
/// A repair capitalised at its completion in the month (Phase M7d, MS-30): its frozen credit lines, and the cost the register's
/// movement added to the pool (<see cref="RegisterCost"/>), which the entry's asset debit must equal.
/// </summary>
public sealed record MaintenancePracticeCapitalisation(
    Guid JobId, string Number, Guid BranchId, DateOnly Day, decimal RegisterCost, IReadOnlyList<MaintenanceCapitalisedCredit> Credits);

/// <summary>The accountant's choices the maintenance section follows, as saved.</summary>
/// <param name="LabourInCost">MS-10 keeps labour in a job's cost (not "parts and outside work only").</param>
public sealed record MaintenancePracticeChoices(MaintenanceLabourBooks LabourInBooks, MaintenanceOpenJobs OpenJobs, bool LabourInCost = true);

/// <summary>
/// The maintenance section of the practice ledger (Phase M6, docs/maintenance/plan.md §5): the entries the accountant's
/// choices would make for one month of maintenance, computed from the events and written nowhere until a month is posted.
/// Pure: the same jobs and choices give the same entries, to the last decimal. It reuses manufacturing's
/// <see cref="PracticeLine"/>, <see cref="PracticeEntry"/> (its order is the job), <see cref="PracticeCheck"/> and
/// <see cref="PracticeLedgerResult"/>.
/// <list type="bullet">
/// <item><b>Parts issued</b> (MJI-): Dr <c>maintenance.own_parts</c> (internal and recharged jobs, MS-30 "always an expense") or
/// <c>maintenance.customer_parts</c> (customer jobs) / Cr the part's stock, per document and stock group. <b>Returned</b> (MJR-):
/// the issue turned round. A document already posted at issue (MS-32) is not here.</item>
/// <item><b>Labour</b>: none by default (MS-33 "report only": payroll already charged 631/635). "Moved into jobs": the month's
/// increase per job, Dr own repairs (own equipment) or the service cost (customer) / Cr <c>maintenance.labour_absorbed</c>; and
/// the overhead the same way against <c>maintenance.overhead_absorbed</c> when MS-10 includes it.</item>
/// <item><b>Open customer jobs</b> (MS-36): none by default (left in expenses, listed). "Work in progress monthly": each open
/// customer job's cost to date at the month's end, Dr <c>maintenance.wip</c> / Cr <c>maintenance.service_cost</c>, reversed on the
/// 1st; "at year end": December's end only, reversed on 1 January.</item>
/// <item><b>Guarantee provision</b> (MS-37 "provision", Phase M7c, <see cref="MaintenanceGuaranteeProvision"/>): booked at a
/// customer job's invoice, used by its reworks, released when its guarantee ends.</item>
/// <item>Outside work, the return note's recharge, the scrap move and a customer job's invoice post through their own documents:
/// nothing here (the invoices are listed for the tie-out).</item>
/// </list>
/// </summary>
public static class MaintenancePracticeLedger
{
    public const string KindIssue = "parts_issue";
    public const string KindReturn = "parts_return";
    public const string KindLabour = "labour_moved";
    public const string KindOverhead = "overhead_moved";
    public const string KindMonthEndWip = "month_end_wip";
    public const string KindWipReversal = "wip_reversal";
    /// <summary>Phase M7d (MS-30): a repair capitalised at completion, Dr the register's asset / Cr where its cost was charged.</summary>
    public const string KindCapitalised = "capitalised_repair";
    /// <summary>The slot of the register's asset account (REN-12), which a capitalised repair debits.</summary>
    public const string AssetSlot = "fixed_assets.asset";

    public const string CheckBalanced = PracticeLedger.CheckBalanced;
    public const string CheckParts = "parts_tie";
    public const string CheckWorkInProgress = PracticeLedger.CheckWorkInProgress;
    public const string CheckInvoices = "invoices_tie";
    /// <summary>MS-37 "provision": the liability's movement equals booked − used − released.</summary>
    public const string CheckProvision = "provision_tie";
    /// <summary>Phase M7d (MS-30): the asset debited by the month's capitalised repairs equals what the register added to the pools.</summary>
    public const string CheckCapitalised = "capitalised_tie";

    /// <summary>Nothing is posted until the accountant posts the month (always said).</summary>
    public const string NotePracticeOnly = PracticeLedger.NotePracticeOnly;
    public const string NoteMonthToDate = PracticeLedger.NoteMonthToDate;
    /// <summary>MS-33 "report only": the hours stay in the wage charges.</summary>
    public const string NoteLabourReportOnly = "labour_report_only";
    /// <summary>MS-33 "moved", but MS-10 leaves labour out of a job's cost: nothing to move.</summary>
    public const string NoteLabourNotInCost = "labour_not_in_cost";
    /// <summary>MS-36 "left in expenses": open customer jobs are listed, not carried.</summary>
    public const string NoteOpenJobsListed = "open_jobs_listed";
    /// <summary>Outside work, recharges, scrap and invoices post through their own documents.</summary>
    public const string NoteOwnDocuments = "own_documents_post";

    public static PracticeLedgerResult Build(
        MaintenancePracticeChoices choices, int year, int month, IReadOnlyList<MaintenancePracticeJob> jobs,
        IReadOnlyList<MaintenancePracticeInvoice> invoices, bool monthToDate = false,
        IReadOnlyList<MaintenanceProvisionJob>? provisions = null, IReadOnlyList<MaintenancePracticeCapitalisation>? capitalisations = null)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(invoices);
        var first = new DateOnly(year, month, 1);
        var last = first.AddMonths(1).AddDays(-1);
        var wipOn = choices.OpenJobs != MaintenanceOpenJobs.LeftInExpensesListed;
        var monthly = choices.OpenJobs == MaintenanceOpenJobs.WorkInProgressMonthly;
        var carryAtEnd = wipOn && (monthly || month == 12);
        var reverseAtStart = wipOn && (monthly || month == 1);
        var labourMoved = choices.LabourInBooks == MaintenanceLabourBooks.MovedIntoJobs;
        var entries = new List<PracticeEntry>();
        decimal wipExpected = 0m;

        foreach (var j in jobs.OrderBy(j => j.Number, StringComparer.Ordinal))
        {
            void Add(string kind, DateOnly day, string? document, params PracticeLine[] lines)
            {
                var kept = lines.Where(l => l.Debit != 0m || l.Credit != 0m).ToList();
                if (kept.Count > 0) entries.Add(new PracticeEntry(kind, day, j.JobId, j.Number, j.BranchId, document, kept));
            }

            // The work in progress carried from last month comes back on the 1st.
            if (reverseAtStart && j.Customer && j.OpenAtStart)
            {
                Add(KindWipReversal, first, null, Pair(MaintenanceAccountSlots.ServiceCost, MaintenanceAccountSlots.WorkInProgress, j.CostAtStart));
                wipExpected -= j.CostAtStart;
            }

            foreach (var doc in j.Documents.OrderBy(d => d.Day).ThenBy(d => d.Number, StringComparer.Ordinal))
                Add(doc.IsReturn ? KindReturn : KindIssue, doc.Day, doc.Number, [.. DocumentLines(j.Customer, doc.IsReturn, doc.Values)]);

            if (labourMoved)
            {
                var day = j.ClosedDay ?? last;
                var service = j.Customer ? MaintenanceAccountSlots.ServiceCost : MaintenanceAccountSlots.OwnParts;
                Add(KindLabour, day, null, Pair(service, MaintenanceAccountSlots.LabourAbsorbed, j.LabourMoved));
                Add(KindOverhead, day, null, Pair(service, MaintenanceAccountSlots.OverheadAbsorbed, j.OverheadMoved));
            }

            if (carryAtEnd && j.Customer && j.OpenAtEnd)
            {
                Add(KindMonthEndWip, last, null, Pair(MaintenanceAccountSlots.WorkInProgress, MaintenanceAccountSlots.ServiceCost, j.CostAtEnd));
                wipExpected += j.CostAtEnd;
            }
        }

        // MS-37 "provision" (Phase M7c): booked at the invoice, used by reworks, released at the guarantee's end.
        decimal provisionExpected = 0m;
        foreach (var p in (provisions ?? []).OrderBy(p => p.Number, StringComparer.Ordinal))
        {
            var (provisionEntries, movement) = MaintenanceGuaranteeProvision.Month(p, last);
            entries.AddRange(provisionEntries);
            provisionExpected += movement;
        }

        // MS-30 "capitalised above an amount" (Phase M7d): Dr the pool's asset / Cr each account the repair's cost was charged to.
        foreach (var c in (capitalisations ?? []).OrderBy(c => c.Day).ThenBy(c => c.Number, StringComparer.Ordinal))
        {
            var lines = new List<PracticeLine> { new(AssetSlot, c.Credits.Sum(x => x.Amount), 0m) };
            lines.AddRange(c.Credits.Where(x => x.Amount != 0m)
                .Select(x => x.Amount > 0m ? new PracticeLine(x.Slot, 0m, x.Amount) : new PracticeLine(x.Slot, -x.Amount, 0m)));
            var kept = lines.Where(l => l.Debit != 0m || l.Credit != 0m).ToList();
            if (kept.Count > 0) entries.Add(new PracticeEntry(KindCapitalised, c.Day, c.JobId, c.Number, c.BranchId, null, kept));
        }

        // ---------- checks: the events against the entries (the R8 pattern) ----------

        var all = entries.SelectMany(e => e.Lines).ToList();
        decimal Net(string slot) => all.Where(l => l.Slot == slot).Sum(l => l.Debit - l.Credit);
        var stockSlots = Enum.GetValues<MaintenanceStockGroup>().Select(StockSlot).Distinct().ToList();
        var partsExpected = jobs.SelectMany(j => j.Documents).Sum(d => (d.IsReturn ? -1m : 1m) * d.Values.Sum(v => v.Value))
            // Parts a capitalised repair takes straight from stock (issued before the practice ledger posts) leave stock here too.
            + (capitalisations ?? []).SelectMany(c => c.Credits).Where(x => stockSlots.Contains(x.Slot)).Sum(x => x.Amount);
        var unbalanced = entries.Count(e => e.Lines.Sum(l => l.Debit) != e.Lines.Sum(l => l.Credit));
        var checks = new List<PracticeCheck>
        {
            new(CheckBalanced, unbalanced == 0, 0m, unbalanced),
            Check(CheckParts, partsExpected, -stockSlots.Sum(Net)),
            Check(CheckWorkInProgress, wipExpected, Net(MaintenanceAccountSlots.WorkInProgress)),
            // Each customer job's invoice of the month names a customer job, invoiced once: counted, not valued (its poster valued it).
            new(CheckInvoices, invoices.All(i => i.JobMatched), invoices.Count, invoices.Count(i => i.JobMatched)),
        };
        // The provision held moves by what was booked less what reworks used and what was released.
        if (provisions is { Count: > 0 })
            checks.Add(Check(CheckProvision, provisionExpected, -Net(MaintenanceAccountSlots.ProvisionLiability)));
        if (capitalisations is { Count: > 0 })
            checks.Add(Check(CheckCapitalised, capitalisations.Sum(c => c.RegisterCost), Net(AssetSlot)));

        var notes = new List<string> { NotePracticeOnly };
        if (monthToDate) notes.Add(NoteMonthToDate);
        if (!labourMoved) notes.Add(NoteLabourReportOnly);
        else if (!choices.LabourInCost) notes.Add(NoteLabourNotInCost);
        if (!wipOn) notes.Add(NoteOpenJobsListed);
        notes.Add(NoteOwnDocuments);
        return new PracticeLedgerResult(entries, [], checks, notes);
    }

    /// <summary>The slot a part's stock group is credited to.</summary>
    public static string StockSlot(MaintenanceStockGroup group) => group switch
    {
        MaintenanceStockGroup.RawMaterial => ManufacturingAccountSlots.RawMaterialStock,
        MaintenanceStockGroup.Supplies => ManufacturingAccountSlots.SuppliesStock,
        MaintenanceStockGroup.SemiFinished => ManufacturingAccountSlots.SemiFinishedStock,
        MaintenanceStockGroup.Finished => ManufacturingAccountSlots.FinishedGoodsStock,
        _ => MaintenanceAccountSlots.MerchandiseStock,
    };

    /// <summary>Which stock a part's role leaves: consumables, accessories and spare parts are supplies; no role, merchandise.</summary>
    public static MaintenanceStockGroup GroupOf(ManufacturingRole role) => role switch
    {
        ManufacturingRole.RawMaterial => MaintenanceStockGroup.RawMaterial,
        ManufacturingRole.Consumable or ManufacturingRole.Accessory or ManufacturingRole.SparePart => MaintenanceStockGroup.Supplies,
        ManufacturingRole.SemiFinished => MaintenanceStockGroup.SemiFinished,
        ManufacturingRole.Finished => MaintenanceStockGroup.Finished,
        _ => MaintenanceStockGroup.Merchandise,
    };

    /// <summary>
    /// One parts document's entry lines, the same whether the practice ledger carries it or it posts at issue (MS-32): Dr the
    /// job's charge slot / Cr each stock group's slot, by the values given (rounded by the caller, once per group).
    /// </summary>
    public static IReadOnlyList<PracticeLine> DocumentLines(bool customer, bool isReturn, IEnumerable<MaintenancePracticeValue> values)
    {
        var charge = customer ? MaintenanceAccountSlots.CustomerParts : MaintenanceAccountSlots.OwnParts;
        return values.OrderBy(v => v.Group).SelectMany(v => Pair(charge, StockSlot(v.Group), isReturn ? -v.Value : v.Value)).ToList();
    }

    /// <summary>Debit one slot and credit the other; a negative amount turns the entry round, a zero makes none.</summary>
    private static PracticeLine[] Pair(string debit, string credit, decimal amount) => amount switch
    {
        > 0m => [new PracticeLine(debit, amount, 0m), new PracticeLine(credit, 0m, amount)],
        < 0m => [new PracticeLine(credit, -amount, 0m), new PracticeLine(debit, 0m, -amount)],
        _ => [],
    };

    private static PracticeCheck Check(string code, decimal expected, decimal actual) => new(code, expected == actual, expected, actual);
}
