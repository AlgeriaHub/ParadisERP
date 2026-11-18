using System.Globalization;
using ParadisERP.Application.Maintenance;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Catalog;
using ParadisERP.Infrastructure.FixedAssets;
using ParadisERP.Infrastructure.Manufacturing;
using ParadisERP.Infrastructure.Payroll;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Maintenance;

/// <summary>
/// MS-30 "capitalised above an amount" (Phase M7d, MNT-01): at a repair job's completion, a repair on the company's own hire
/// equipment whose cost (M4's C) is above the amount the accountant set is added to the equipment's pool in the REN-12 register,
/// and the maintenance practice ledger of the completion month moves its cost out of the accounts it was charged to and into the
/// asset. Below the amount, or whenever a condition is missing, today's expense treatment stays and nothing here writes.
/// <para><b>When</b> (all of them): MS-30 says so with an amount above 0; an internal or recharged job (never a customer's
/// equipment); units repaired (a job that only scrapped units improved nothing); the register holds the product (switched over,
/// a hire product) and its pool at the job's branch holds units; MS-39 names the first month the maintenance practice ledger posts
/// and the job completes in it or after (the entry posts with that ledger; a month that never posts would leave the register
/// carrying a cost the books do not); C &gt; the amount.</para>
/// <para><b>What the pool receives</b> — A, the sum of the lines below, frozen at completion
/// (<see cref="MaintenanceJobCapitalisedLine"/>). The entry, in the practice ledger of the completion month on the completion day:
/// Dr <c>fixed_assets.asset</c> A / Cr each line.</para>
/// <list type="bullet">
/// <item><b>Parts</b>, per issue (return: negative) document, at the values frozen on its lines, rounded once per stock group as
/// the practice ledger rounds them: a document that reaches the books through maintenance — posted at issue (MS-32), or of a
/// month on or after MS-39's first month (the practice ledger posts it Dr <c>maintenance.own_parts</c>) — is taken back from
/// <c>maintenance.own_parts</c>; an older document never left the stock accounts in the books, so the asset takes it straight from
/// its stock group's slot.</item>
/// <item><b>Labour and overhead</b> (when MS-10 puts them in the cost): under MS-33 "moved into jobs", the hours of the months the
/// practice ledger posts were moved Dr <c>maintenance.own_parts</c> / Cr labour (overhead) absorbed, so they are taken back from
/// <c>maintenance.own_parts</c>; the rest — every hour under MS-33 "report only", and hours before MS-39's month — sits in the
/// wage charges (631/635) and is credited to <c>maintenance.capitalised</c> (own work capitalised, SCF 73x). Valued as M4
/// values them at completion.</item>
/// <item><b>Outside work</b> whose payment voucher is paid by completion: the voucher debited its expense account (category
/// repair → 615), so it is credited back to that account (<c>account:&lt;code&gt;</c>), net of the recoverable VAT as the job's
/// cost counts it. Outside work not yet paid (or with no voucher) is not capitalised: it stays an expense when its voucher
/// posts.</item>
/// </list>
/// Nothing is counted twice: each amount debited to an expense or stock account by its own document is credited back once, and
/// the asset receives exactly the sum. The register's movement and the lines are written in the completion's transaction.
/// </summary>
public sealed class MaintenanceCapitalisation(PosDbContext db, IRegionalSettingsProvider regional)
{
    public const string ElementParts = "parts";
    public const string ElementLabour = "labour";
    public const string ElementOverhead = "overhead";
    public const string ElementOutsideWork = "outside_work";

    /// <summary>What happened at completion: capitalised (with the amount), or why the repair stays an expense (null when MS-30 is off).</summary>
    public sealed record Outcome(bool Capitalised, decimal Amount, string? Detail);

    /// <summary>
    /// Called by the job writer at completion, inside its transaction and under its lock, after the units moved (so units found
    /// beyond repair have left the pool and carry none of the repair). Writes the register movement, the job's capitalisation
    /// and its lines when the repair is capitalised; writes nothing otherwise. Never refuses the completion.
    /// </summary>
    public async Task<Outcome?> AtCompletionAsync(MaintenanceJob job, MaintenanceSettings settings, DateTime completedAtUtc, string? by,
        CancellationToken ct = default)
    {
        if (settings.OwnRepairs != MaintenanceOwnRepairs.CapitalisedAboveAmount || settings.CapitaliseAboveAmount <= 0m) return null;
        Outcome Expense(string why) => new(false, 0m, "kept an expense: " + why);
        if (job.Kind == MaintenanceJobKind.Customer || job.ProductId is not Guid productId) return Expense("not the company's equipment");
        if (job.RepairedQty <= 0m) return Expense("no unit repaired");
        var zone = StoreDocumentHistory.Zone(regional);
        var completedMonth = MonthOf(completedAtUtc, zone);
        if (settings.PostingFromMonth is not DateOnly fromMonth || completedMonth < fromMonth)
            return Expense("the practice ledger does not post this month (MS-39)");

        var book = new FixedAssetBook(db);
        if (!await book.HoldsAsync(productId, ct)) return Expense("the register does not hold this equipment");
        if (await book.PoolUnitsAsync(productId, job.BranchId, ct) <= 0m) return Expense("its pool holds no units");

        var costing = new MaintenanceCosting(db, new LabourRates(db, new PayrollSettingsStore(db, regional), regional), regional);
        var basis = await costing.CapitalisationBasisAsync(job, fromMonth, ct);
        if (basis.Total <= settings.CapitaliseAboveAmount)
            return Expense($"cost {Amount(basis.Total)} not above {Amount(settings.CapitaliseAboveAmount)}");

        var lines = new List<MaintenanceJobCapitalisedLine>();
        void Add(string element, string slot, decimal amount)
        {
            if (amount == 0m) return;
            var same = lines.FirstOrDefault(l => l.Element == element && l.Slot == slot);
            if (same is not null) same.Amount += amount;
            else lines.Add(new MaintenanceJobCapitalisedLine { JobId = job.Id, Element = element, Slot = slot, Amount = amount });
        }

        // Parts, per document, as the books carry each one.
        var parts = await db.MaintenanceJobParts.AsNoTracking().Where(p => p.JobId == job.Id)
            .Select(p => new { p.DocumentId, p.Kind, p.AtUtc, p.Product!.ManufacturingRole, p.Value }).ToListAsync(ct);
        var postedAtIssue = await MaintenancePartsAtIssue.PostedAsync(db, parts.Select(p => p.DocumentId).Distinct().ToList(), ct);
        foreach (var doc in parts.GroupBy(p => p.DocumentId))
        {
            var sign = doc.First().Kind == MaintenancePartKind.Return ? -1m : 1m;
            var values = MaintenancePartsAtIssue.Values(doc.Select(p => new MaintenancePartsAtIssue.Line(p.ManufacturingRole, p.Value)));
            var inBooks = postedAtIssue.Contains(doc.Key) || MonthOf(doc.First().AtUtc, zone) >= fromMonth;
            foreach (var v in values)
                Add(ElementParts, inBooks ? MaintenanceAccountSlots.OwnParts : MaintenancePracticeLedger.StockSlot(v.Group), sign * v.Value);
        }

        // Labour and overhead: moved into own repairs for the months the practice ledger posts, otherwise in the wage charges.
        var moved = settings.LabourInBooks == MaintenanceLabourBooks.MovedIntoJobs;
        Add(ElementLabour, moved ? MaintenanceAccountSlots.OwnParts : MaintenanceAccountSlots.Capitalised, basis.Labour - basis.LabourBefore);
        Add(ElementLabour, MaintenanceAccountSlots.Capitalised, basis.LabourBefore);
        Add(ElementOverhead, moved ? MaintenanceAccountSlots.OwnParts : MaintenanceAccountSlots.Capitalised, basis.Overhead - basis.OverheadBefore);
        Add(ElementOverhead, MaintenanceAccountSlots.Capitalised, basis.OverheadBefore);

        // Outside work paid by now: back to the account its voucher debited, net of the recoverable VAT as the cost counts it.
        var expenses = await db.MaintenanceJobExpenses.AsNoTracking().Where(e => e.JobId == job.Id && e.PaymentVoucherId != null)
            .Join(db.PaymentVouchers, e => e.PaymentVoucherId, v => (Guid?)v.Id, (e, v) => new { e.Amount, v.Status, v.ExpenseAccountCode })
            .ToListAsync(ct);
        foreach (var e in expenses.Where(e => e.Status == PaymentVoucherStatus.Paid && !string.IsNullOrEmpty(e.ExpenseAccountCode)))
            Add(ElementOutsideWork, MaintenanceAccountSlots.Account(e.ExpenseAccountCode!),
                Ledger(basis.ExpenseVatPercent > 0m ? e.Amount * 100m / (100m + basis.ExpenseVatPercent) : e.Amount));

        lines.RemoveAll(l => l.Amount == 0m);
        var amount = lines.Sum(l => l.Amount);
        if (amount <= 0m) return Expense("nothing of its cost is in the books yet");

        var product = await db.Products.AsNoTracking().Where(p => p.Id == productId).Select(p => p.Sku).FirstOrDefaultAsync(ct);
        var movement = await book.CapitaliseRepairAsync(new FixedAssetEvent(productId, job.BranchId, job.RepairedQty, product, job.Id,
            job.Number, "Capitalised repair", by, job.ItemReference), amount, ct);
        db.MaintenanceJobCapitalisedLines.AddRange(lines);
        job.CapitalisedAtUtc = completedAtUtc;
        job.CapitalisedAmount = movement.Cost;
        job.CapitalisedMovementId = movement.Id;
        return new Outcome(true, movement.Cost, $"capitalised {Amount(movement.Cost)} (cost {Amount(basis.Total)} above {Amount(settings.CapitaliseAboveAmount)})");
    }

    private static DateOnly MonthOf(DateTime utc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);
        return new DateOnly(local.Year, local.Month, 1);
    }

    private static decimal Ledger(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);

    private static string Amount(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
