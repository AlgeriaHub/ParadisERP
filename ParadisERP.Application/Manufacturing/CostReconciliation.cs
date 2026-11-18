using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ParadisERP.Application.Manufacturing;

/// <summary>A stored B1 element line: its element, frozen total (3 dp) and the status it was frozen with.</summary>
public sealed record StoredElement(string Element, decimal TotalCost, string Status);

/// <summary>One element's signed contribution to the order's cost: frozen, recognized late effect, and the two together.</summary>
public sealed record ElementLine(string Element, string Status, decimal Original, decimal LateRecognized, decimal Adjusted);

/// <summary>A late-cost record as B1 stored it.</summary>
public sealed record LateInput(Guid Id, Guid SheetId, string Kind, string SourceReference, decimal Amount, DateTime RecordedAtUtc);

/// <summary>What a late cost means for the provisional cost: the element it names, how it is treated, and why.</summary>
public sealed record LateAssessment(Guid Id, string? Element, string Treatment, IReadOnlyList<string> Flags);

public sealed record LateTotals(decimal Recorded, decimal Recognized, decimal Unclassified, decimal Uncertain);

public sealed record CheckResult(string Code, string Result, decimal? Expected = null, decimal? Actual = null, string? Detail = null);

/// <summary>A stored release value with what the release document itself says (its number, moment and catalogue cost).</summary>
public sealed record StoredRelease(Guid ReleaseId, string Number, DateTime AtUtc, decimal Quantity, decimal Value, decimal UnitValue, decimal? CatalogueUnitCost);

public sealed record ReleaseLine(
    Guid ReleaseId, string Number, DateTime AtUtc, decimal Quantity, decimal Value, decimal UnitValue, bool CarriesRemainder,
    decimal? CatalogueReferenceValue);

public sealed record ReleaseSummary(
    IReadOnlyList<ReleaseLine> Lines, decimal AcceptedQty, decimal ReleasedQty, decimal UnreleasedAcceptedQty,
    decimal ReleasedValue, decimal KeptInCharges, decimal LateNotAllocated, IReadOnlyList<string> Flags);

public sealed record ScrapFigures(
    decimal AcceptedQty, decimal RejectedQty, decimal? RejectionRatePercent, decimal? MaterialScrapValue,
    decimal RecoveryValue, string RecoveryStatus, decimal? SpoilageAbsorbed, decimal? MaterialWastage, string? Unavailable);

/// <summary>A variance line. <c>Detail</c> lines split a group line and carry no late effect.</summary>
public sealed record VarianceLine(string Component, bool Detail, decimal? Original, decimal? LateRecognized, decimal? Adjusted);

public sealed record VarianceFigures(bool Available, string? Reason, IReadOnlyList<VarianceLine> Lines);

public sealed record ElementDrift(string Element, decimal Adjusted, decimal Live, decimal Unexplained);

public sealed record GapItem(string Code, string Severity, string? Element = null, decimal? Value = null, string? Detail = null);

/// <summary>
/// Manufacturing costing B2 (docs/manufacturing/costing-b2-plan.md): the reconciliation of a B1 sheet, pure —
/// no database, no clock, the same input giving the same figures on SQLite and SQL Server.
///
/// <para>
/// It reuses B1's own rules and never adds one: the elements add up to the frozen total (C = M + K + A + L + O + E − R)
/// with the rounding residual shown, the last release of a completed order carries the remainder, a completed order
/// with nothing released keeps its cost in the charges, rejects are absorbed. A late cost is never allocated to a
/// release, a unit, a spoilage figure or an account: it is recognized against the element its kind names, or kept
/// apart as unclassified or uncertain. Nothing here is financially final.
/// </para>
/// <para>
/// Money is rounded once, where it is shown, exactly as B1 rounds it: three decimals, unit values six, away from
/// zero. Sums of stored (already rounded) values are never rounded again.
/// </para>
/// </summary>
public static partial class CostReconciliation
{
    public const string Materials = "materials";
    public const string Consumables = "consumables";
    public const string Accessories = "accessories";
    public const string Labour = "labour";
    public const string Overhead = "overhead";
    public const string Expenses = "expenses";
    public const string Recovery = "recovery";

    /// <summary>B1's seven element lines, in the order the sheet writes them.</summary>
    public static readonly IReadOnlyList<string> Elements = [Materials, Consumables, Accessories, Labour, Overhead, Expenses, Recovery];

    public const string TreatmentRecognized = "recognized";
    public const string TreatmentUnclassified = "unclassified";
    public const string TreatmentUncertain = "uncertain";

    public const string FlagDuplicateSource = "duplicate_source";
    public const string FlagSharedSource = "shared_source";
    public const string FlagOverlapsOriginal = "overlaps_original";
    public const string FlagSeveralOrders = "source_on_several_orders";

    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string NotApplicable = "not_applicable";

    public const string CheckSourceComplete = "source_complete";
    public const string CheckSourceHash = "source_hash";
    public const string CheckSourceReplay = "source_replay";
    public const string CheckElementIdentity = "element_identity";
    public const string CheckMaterialRows = "material_rows";
    public const string CheckExpenseRows = "expense_rows";
    public const string CheckLabourRows = "labour_rows";
    public const string CheckScrapRow = "scrap_row";
    public const string CheckReleaseSum = "release_sum";
    public const string CheckUnitCost = "unit_cost";
    public const string CheckLateSources = "late_sources";

    public const string FlagUnreleasedOutput = "unreleased_output";
    public const string FlagOverReleased = "over_released";
    public const string FlagFinalReleaseNegative = "final_release_negative";

    public const string SeverityMissing = "missing";
    public const string SeverityIntegrity = "integrity";
    public const string SeverityIncomplete = "incomplete";
    public const string SeverityProvisional = "provisional";
    public const string SeverityReview = "review";
    public const string SeverityInfo = "info";

    /// <summary>Half a unit in the last place of a 3-decimal amount: what one rounding can move a figure by.</summary>
    public const decimal HalfMill = 0.0005m;

    public static decimal Money(decimal value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
    public static decimal Unit(decimal value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
    public static decimal Percent(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>+1 for a cost element, −1 for recovery, which reduces the cost.</summary>
    public static int Sign(string element) => element == Recovery ? -1 : 1;

    // ---------- late costs ----------

    /// <summary>
    /// The element a late cost's free-text kind names (trimmed, lower-cased invariant), or null: unknown kinds are
    /// unclassified, never guessed from the reason.
    /// </summary>
    public static string? ElementOf(string? kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        "material" or "materials" => Materials,
        "consumable" or "consumables" => Consumables,
        "accessory" or "accessories" => Accessories,
        "labour" or "labor" => Labour,
        "overhead" => Overhead,
        "expense" or "expenses" => Expenses,
        "recovery" or "scrap_recovery" => Recovery,
        _ => null,
    };

    /// <summary>A reference as compared: spaces collapsed, trimmed, upper-cased invariant (SQL Server's default collation ignores case; SQLite does not).</summary>
    public static string NormalizeSource(string? source) => Spaces().Replace((source ?? "").Trim(), " ").ToUpperInvariant();

    /// <summary>The tokens of a normalized reference a frozen identity can match: split on spaces and : / # , ; (never on '-', which voucher numbers carry).</summary>
    public static IEnumerable<string> SourceTokens(string normalized) =>
        normalized.Split([' ', ':', '/', '#', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Append(normalized);

    /// <summary>
    /// How each late cost counts. Recognized: its kind names an element and nothing makes its effect doubtful.
    /// Unclassified: its kind names no element. Uncertain: it repeats another record's source for the same element
    /// (case or spacing aside), or names a source already inside the frozen sheet — counting it could count a cost
    /// twice. Uncertain and unclassified amounts stay visible and outside the adjusted provisional total.
    /// <paramref name="frozenSources"/> gives a sheet's normalized source identities (expense ids, voucher ids and numbers).
    /// </summary>
    public static IReadOnlyDictionary<Guid, LateAssessment> AssessLate(
        IReadOnlyList<LateInput> late, Func<Guid, IReadOnlySet<string>> frozenSources)
    {
        var rows = late.Select(l => new
        {
            l.Id, l.SheetId, Element = ElementOf(l.Kind), KindKey = (l.Kind ?? "").Trim().ToLowerInvariant(),
            Source = NormalizeSource(l.SourceReference),
        }).ToList();
        string Group(string? element, string kindKey) => element ?? "?" + kindKey;
        var duplicates = rows.GroupBy(r => (r.SheetId, G: Group(r.Element, r.KindKey), r.Source)).Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(r => r.Id)).ToHashSet();
        var shared = rows.GroupBy(r => (r.SheetId, r.Source)).Where(g => g.Select(r => Group(r.Element, r.KindKey)).Distinct().Count() > 1)
            .SelectMany(g => g.Select(r => r.Id)).ToHashSet();
        var several = rows.GroupBy(r => r.Source).Where(g => g.Select(r => r.SheetId).Distinct().Count() > 1)
            .SelectMany(g => g.Select(r => r.Id)).ToHashSet();

        var result = new Dictionary<Guid, LateAssessment>(rows.Count);
        foreach (var r in rows)
        {
            var flags = new List<string>();
            if (duplicates.Contains(r.Id)) flags.Add(FlagDuplicateSource);
            if (frozenSources(r.SheetId) is { Count: > 0 } frozen && SourceTokens(r.Source).Any(frozen.Contains)) flags.Add(FlagOverlapsOriginal);
            if (shared.Contains(r.Id)) flags.Add(FlagSharedSource);
            if (several.Contains(r.Id)) flags.Add(FlagSeveralOrders);
            flags.Sort(StringComparer.Ordinal);
            var treatment = r.Element is null ? TreatmentUnclassified
                : flags.Contains(FlagDuplicateSource) || flags.Contains(FlagOverlapsOriginal) ? TreatmentUncertain
                : TreatmentRecognized;
            result[r.Id] = new LateAssessment(r.Id, r.Element, treatment, flags);
        }
        return result;
    }

    public static LateTotals Totals(IEnumerable<(decimal Amount, string Treatment)> rows)
    {
        decimal recorded = 0m, recognized = 0m, unclassified = 0m, uncertain = 0m;
        foreach (var (amount, treatment) in rows)
        {
            recorded += amount;
            switch (treatment)
            {
                case TreatmentRecognized: recognized += amount; break;
                case TreatmentUnclassified: unclassified += amount; break;
                default: uncertain += amount; break;
            }
        }
        return new LateTotals(recorded, recognized, unclassified, uncertain);
    }

    // ---------- elements ----------

    /// <summary>
    /// Each element's signed contribution (recovery negative), the recognized late effect on it, and the residual
    /// ρ = O − Σ signed elements that rounding each element separately leaves (|ρ| ≤ 7 × 0.0005 on a sound sheet).
    /// Σ adjusted lines + ρ = O + Σ recognized late = the adjusted provisional total.
    /// </summary>
    public static (IReadOnlyList<ElementLine> Lines, decimal Residual) ReconcileElements(
        decimal original, IReadOnlyList<StoredElement> stored, IReadOnlyDictionary<string, decimal> recognizedByElement)
    {
        var lines = Elements.Select(e =>
        {
            var row = stored.FirstOrDefault(s => s.Element == e);
            var signed = row is null ? 0m : Sign(e) * row.TotalCost;
            var late = recognizedByElement.GetValueOrDefault(e);
            return new ElementLine(e, row?.Status ?? "missing", signed, late, signed + late);
        }).ToList();
        return (lines, original - lines.Sum(l => l.Original));
    }

    // ---------- releases ----------

    /// <summary>
    /// B1's releases in the engine's order (moment, then number). The last release of a completed order carries
    /// the remainder; with none, the whole cost stays in the charges (D13). Recognized late effects are reported
    /// as not allocated: no approved policy assigns them to a release (C-10).
    /// </summary>
    public static ReleaseSummary Releases(decimal original, decimal acceptedQty, IReadOnlyList<StoredRelease> stored, decimal lateRecognized)
    {
        var ordered = stored.OrderBy(r => r.AtUtc).ThenBy(r => r.Number, StringComparer.Ordinal)
            .ThenBy(r => r.ReleaseId.ToString(), StringComparer.Ordinal).ToList();
        var lines = ordered.Select((r, i) => new ReleaseLine(r.ReleaseId, r.Number, r.AtUtc, r.Quantity, r.Value, r.UnitValue,
            CarriesRemainder: i == ordered.Count - 1,
            r.CatalogueUnitCost is decimal unit ? Money(r.Quantity * unit) : null)).ToList();
        var releasedQty = lines.Sum(l => l.Quantity);
        var released = lines.Sum(l => l.Value);
        var flags = new List<string>();
        if (acceptedQty - releasedQty > 0m) flags.Add(FlagUnreleasedOutput);
        if (acceptedQty - releasedQty < 0m) flags.Add(FlagOverReleased);
        if (lines.Count > 0 && lines[^1].Value < 0m) flags.Add(FlagFinalReleaseNegative);
        return new ReleaseSummary(lines, acceptedQty, releasedQty, acceptedQty - releasedQty, released,
            lines.Count == 0 ? original : 0m, lateRecognized, flags);
    }

    // ---------- scrap and rejects ----------

    /// <summary>
    /// Rejected output under B1's rule (D23): rejects are absorbed in the cost, so spoilage is part of C, never added
    /// to it — (C + R) × Qr ÷ (Qa + Qr) − R on the frozen C and R. Material scrap value and wastage (usage variance +
    /// scrap value) come from the replayed frozen inputs. No late cost is allocated to spoilage.
    /// </summary>
    public static ScrapFigures Scrap(
        decimal original, decimal acceptedQty, decimal rejectedQty, decimal recoveryValue, string recoveryStatus,
        ProductionCostSheet? replay, string? unavailable)
    {
        var output = acceptedQty + rejectedQty;
        decimal? rate = output > 0m ? Percent(rejectedQty / output * 100m) : null;
        decimal? spoilage = output > 0m ? Money((original + recoveryValue) * rejectedQty / output - recoveryValue) : null;
        decimal? scrap = replay is null ? null : Money(replay.Materials.Sum(m => m.ScrapValue));
        decimal? wastage = replay?.Variances is null ? null
            : Money(replay.Materials.Sum(m => m.UsageVariance ?? 0m) + replay.Materials.Sum(m => m.ScrapValue));
        return new ScrapFigures(acceptedQty, rejectedQty, rate, scrap, recoveryValue, recoveryStatus, spoilage, wastage,
            replay is null ? unavailable : null);
    }

    // ---------- variances ----------

    /// <summary>
    /// The engine's variances (costing plan §2.11) replayed on the frozen inputs, against the frozen estimate. Group
    /// lines take the recognized late effect of their elements; their detail lines (usage and price, efficiency and
    /// rate) take none, because no rule splits a late cost between them. The labour split is left out when it could
    /// expose a payroll rate (<paramref name="labourSplit"/> false).
    /// </summary>
    public static VarianceFigures Variances(
        ProductionCostSheet? replay, string? unavailable, IReadOnlyDictionary<string, decimal> recognized, bool labourSplit)
    {
        if (replay is null) return new VarianceFigures(false, unavailable, []);
        if (replay.Variances is not { } v) return new VarianceFigures(false, "no_accepted_or_planned_output", []);
        decimal Late(params string[] elements) => elements.Sum(e => recognized.GetValueOrDefault(e));
        VarianceLine Group(string component, decimal original, decimal late) =>
            new(component, false, Money(original), late, Money(original) + late);
        var lines = new List<VarianceLine>
        {
            Group("materials", v.MaterialUsage + v.MaterialPrice, Late(Materials, Consumables, Accessories)),
            new("material_usage", true, Money(v.MaterialUsage), null, null),
            new("material_price", true, Money(v.MaterialPrice), null, null),
            Group("labour", v.LabourEfficiency + v.LabourRate, Late(Labour)),
        };
        if (labourSplit)
        {
            lines.Add(new("labour_efficiency", true, Money(v.LabourEfficiency), null, null));
            lines.Add(new("labour_rate", true, Money(v.LabourRate), null, null));
        }
        lines.Add(Group("overhead", v.Overhead, Late(Overhead)));
        lines.Add(Group("expenses", v.Expenses, Late(Expenses)));
        lines.Add(Group("recovery", v.Recovery, Late(Recovery)));
        var groups = lines.Where(l => !l.Detail).ToList();
        var total = Money(v.Total);
        lines.Add(new("rounding", false, total - groups.Sum(g => g.Original!.Value), null, null));
        var late = groups.Sum(g => g.LateRecognized!.Value);
        lines.Add(new("total", false, total, late, total + late));
        return new VarianceFigures(true, null, lines);
    }

    // ---------- live comparison ----------

    /// <summary>
    /// Today's live computation against the adjusted provisional figures as of the chosen date, element by element.
    /// The unexplained difference is live − adjusted; nothing is recorded or corrected from it.
    /// </summary>
    public static (decimal Live, decimal Unexplained, IReadOnlyList<ElementDrift> Elements) Drift(
        ProductionCostSheet live, IReadOnlyList<ElementLine> adjusted, decimal adjustedTotal)
    {
        var a = live.Actual;
        var values = new Dictionary<string, decimal>
        {
            [Materials] = a.Materials, [Consumables] = a.Consumables, [Accessories] = a.Accessories, [Labour] = a.Labour,
            [Overhead] = a.Overhead, [Expenses] = a.Expenses, [Recovery] = a.Recovery,
        };
        var drift = adjusted.Select(l =>
        {
            var liveSigned = Sign(l.Element) * Money(values[l.Element]);
            return new ElementDrift(l.Element, l.Adjusted, liveSigned, liveSigned - l.Adjusted);
        }).ToList();
        var total = Money(a.Total);
        return (total, total - adjustedTotal, drift);
    }

    // ---------- integrity ----------

    public static string Sha256Hex(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The stored rows the integrity checks read for one sheet.</summary>
    public sealed record SheetRows(
        decimal Original, decimal AcceptedQty, decimal RejectedQty, decimal? UnitCost, string SourceState, string SourceHash, string? SourceJson,
        IReadOnlyList<StoredElement> Elements,
        IReadOnlyList<(Guid OrderMaterialId, decimal Quantity, decimal UnitCost, decimal TotalCost)> Materials,
        IReadOnlyList<(Guid EmployeeId, int Year, int Month, decimal Hours, decimal? Rate, decimal? TotalCost)> Labour,
        IReadOnlyList<(Guid ExpenseId, decimal Amount)> ExpenseRows,
        (decimal RejectedQty, decimal RecoveryValue)? ScrapRow,
        IReadOnlyList<StoredRelease> Releases);

    /// <summary>
    /// Diagnostics only — pass, fail or not applicable, never a repair. A failure says the stored history does not
    /// add up or does not match its own source; it changes nothing.
    /// </summary>
    public static IReadOnlyList<CheckResult> Checks(
        SheetRows s, CostingCaptureSource? source, string? sourceReason, ProductionCostSheet? replay,
        IReadOnlyCollection<LateAssessment> late)
    {
        var checks = new List<CheckResult>();
        var complete = s.SourceState == "complete";

        // Source completeness: captured at the freeze and readable by this method version.
        checks.Add(!complete ? new(CheckSourceComplete, Fail, Detail: "incomplete_legacy")
            : sourceReason == CostingCapture.ReasonVersion ? new(CheckSourceComplete, NotApplicable, Detail: sourceReason)
            : source is null ? new(CheckSourceComplete, Fail, Detail: sourceReason)
            : new(CheckSourceComplete, Pass));

        checks.Add(!complete || s.SourceJson is null ? new(CheckSourceHash, NotApplicable, Detail: CostingCapture.ReasonNotCaptured)
            : string.Equals(Sha256Hex(s.SourceJson), s.SourceHash, StringComparison.OrdinalIgnoreCase)
                ? new(CheckSourceHash, Pass) : new(CheckSourceHash, Fail, Detail: "hash_mismatch"));

        checks.Add(replay is null || source is null ? new(CheckSourceReplay, NotApplicable, Detail: sourceReason)
            : ReplayMismatch(s, replay) is { } mismatch ? new(CheckSourceReplay, Fail, mismatch.Expected, mismatch.Actual, mismatch.What)
            : new(CheckSourceReplay, Pass));

        var signed = s.Elements.Sum(e => Sign(e.Element) * e.TotalCost);
        var residual = s.Original - signed;
        checks.Add(Math.Abs(residual) <= Elements.Count * HalfMill
            ? new(CheckElementIdentity, Pass, s.Original, signed)
            : new(CheckElementIdentity, Fail, s.Original, signed, "residual_beyond_rounding"));

        var materialGroups = s.Elements.Where(e => e.Element is Materials or Consumables or Accessories).Sum(e => e.TotalCost);
        var materialRows = s.Materials.Sum(m => m.TotalCost);
        checks.Add(s.Materials.Count == 0 && materialGroups == 0m ? new(CheckMaterialRows, NotApplicable, Detail: "no_material")
            : Math.Abs(materialGroups - materialRows) <= (s.Materials.Count + 3) * HalfMill
                ? new(CheckMaterialRows, Pass, materialGroups, materialRows)
                : new(CheckMaterialRows, Fail, materialGroups, materialRows));

        var expenses = s.Elements.FirstOrDefault(e => e.Element == Expenses)?.TotalCost ?? 0m;
        var expenseRows = s.ExpenseRows.Sum(e => e.Amount);
        checks.Add(s.ExpenseRows.Count == 0 && expenses == 0m ? new(CheckExpenseRows, NotApplicable, Detail: "no_expense")
            : Math.Abs(expenses - expenseRows) <= s.ExpenseRows.Count * HalfMill
                ? new(CheckExpenseRows, Pass, expenses, expenseRows)
                : new(CheckExpenseRows, Fail, expenses, expenseRows));

        var labour = s.Elements.FirstOrDefault(e => e.Element == Labour)?.TotalCost ?? 0m;
        var labourRows = s.Labour.Sum(l => l.TotalCost ?? 0m);
        checks.Add(s.Labour.Count == 0 && labour == 0m ? new(CheckLabourRows, NotApplicable, Detail: "no_labour")
            : Math.Abs(labour - labourRows) <= (s.Labour.Count + 1) * HalfMill
                ? new(CheckLabourRows, Pass, labour, labourRows)
                : new(CheckLabourRows, Fail, labour, labourRows));

        var recovery = s.Elements.FirstOrDefault(e => e.Element == Recovery)?.TotalCost ?? 0m;
        checks.Add(s.ScrapRow is not { } scrap ? new(CheckScrapRow, Fail, recovery, null, "scrap_row_missing")
            : scrap.RecoveryValue == recovery && scrap.RejectedQty == s.RejectedQty
                ? new(CheckScrapRow, Pass, recovery, scrap.RecoveryValue)
                : new(CheckScrapRow, Fail, recovery, scrap.RecoveryValue));

        var released = s.Releases.Sum(r => r.Value);
        checks.Add(s.Releases.Count == 0 ? new(CheckReleaseSum, NotApplicable, s.Original, null, "no_release_kept_in_charges")
            : released == s.Original ? new(CheckReleaseSum, Pass, s.Original, released)
            : new(CheckReleaseSum, Fail, s.Original, released));

        decimal? unit = s.AcceptedQty > 0m ? Unit(s.Original / s.AcceptedQty) : null;
        checks.Add(unit == s.UnitCost ? new(CheckUnitCost, Pass, unit, s.UnitCost) : new(CheckUnitCost, Fail, unit, s.UnitCost));

        checks.Add(late.Count == 0 ? new(CheckLateSources, NotApplicable, Detail: "no_late_cost")
            : late.Any(l => l.Flags.Contains(FlagDuplicateSource) || l.Flags.Contains(FlagOverlapsOriginal))
                ? new(CheckLateSources, Fail, Detail: "duplicate_or_overlapping_source")
                : new(CheckLateSources, Pass));
        return checks;
    }

    /// <summary>The first stored figure the replayed B1 calculation does not reproduce exactly, after B1's own rounding.</summary>
    private static (string What, decimal? Expected, decimal? Actual)? ReplayMismatch(SheetRows s, ProductionCostSheet replay)
    {
        var a = replay.Actual;
        var expected = new Dictionary<string, decimal>
        {
            [Materials] = a.Materials, [Consumables] = a.Consumables, [Accessories] = a.Accessories, [Labour] = a.Labour,
            [Overhead] = a.Overhead, [Expenses] = a.Expenses, [Recovery] = a.Recovery,
        };
        if (Money(a.Total) != s.Original) return ("total", Money(a.Total), s.Original);
        foreach (var e in Elements)
        {
            var stored = s.Elements.FirstOrDefault(x => x.Element == e);
            if (stored is null || stored.TotalCost != Money(expected[e])) return ($"element:{e}", Money(expected[e]), stored?.TotalCost);
        }
        if (replay.Materials.Count != s.Materials.Count) return ("material_count", replay.Materials.Count, s.Materials.Count);
        foreach (var m in replay.Materials)
        {
            var stored = s.Materials.FirstOrDefault(x => x.OrderMaterialId == m.Id);
            if (stored == default || stored.TotalCost != Money(m.ConsumedValue + m.ScrapValue) || stored.UnitCost != Unit(m.IssueAverage))
                return ($"material:{m.Id}", Money(m.ConsumedValue + m.ScrapValue), stored == default ? null : stored.TotalCost);
        }
        if (replay.Labour.Count != s.Labour.Count) return ("labour_count", replay.Labour.Count, s.Labour.Count);
        foreach (var l in replay.Labour)
        {
            var stored = s.Labour.FirstOrDefault(x => x.EmployeeId == l.EmployeeId && x.Year == l.Year && x.Month == l.Month);
            if (stored == default || stored.Hours != l.Hours || stored.TotalCost != Money(l.Cost)
                || stored.Rate != (l.Rate is decimal rate ? Unit(rate) : null))
                return ("labour", Money(l.Cost), stored == default ? null : stored.TotalCost);
        }
        if (replay.Releases.Count != s.Releases.Count) return ("release_count", replay.Releases.Count, s.Releases.Count);
        var carried = 0m;
        foreach (var r in replay.Releases)
        {
            var value = r.CarriesRemainder ? s.Original - carried : Money(r.Value);
            carried += value;
            var stored = s.Releases.FirstOrDefault(x => x.ReleaseId == r.Id);
            if (stored is null || stored.Value != value || stored.Quantity != r.BaseQty) return ($"release:{r.Id}", value, stored?.Value);
        }
        if (s.ScrapRow is { } scrap && scrap.RecoveryValue != Money(a.Recovery)) return ("recovery", Money(a.Recovery), scrap.RecoveryValue);
        return null;
    }

    // ---------- gaps ----------

    /// <summary>What a frozen sheet is missing or rests on, from its stored statuses, its notes, releases, late costs and checks.</summary>
    public static IReadOnlyList<GapItem> Gaps(
        string kind, string sourceState, int? freezeLagDays, decimal original, IReadOnlyList<StoredElement> elements,
        IReadOnlyList<CostNote> notes, ReleaseSummary releases, IReadOnlyCollection<LateAssessment> late,
        IReadOnlyList<CheckResult> checks, bool labourHoursShown)
    {
        var gaps = new List<GapItem>();
        if (sourceState != "complete") gaps.Add(new("source_incomplete_legacy", SeverityIntegrity));
        if (kind == "reconstructed") gaps.Add(new("reconstructed", SeverityInfo, Value: freezeLagDays));
        else if (freezeLagDays is > 0) gaps.Add(new("freeze_lag", SeverityInfo, Value: freezeLagDays));
        if (original == 0m) gaps.Add(new("zero_cost", SeverityReview));
        foreach (var e in elements)
        {
            var severity = e.Status switch
            {
                ProductionCostEngine.ElementIncomplete or ProductionCostEngine.ElementNotSet or ProductionCostEngine.ElementNotRecorded => SeverityIncomplete,
                ProductionCostEngine.ElementProvisional or ProductionCostEngine.ElementReferenceCost => SeverityProvisional,
                _ => null,
            };
            if (severity is not null) gaps.Add(new($"element_{e.Status}", severity, e.Element));
        }
        // Notes the element statuses, the release flags or the finalization state already say are not repeated.
        foreach (var n in notes.Where(n => n.Code is not (ProductionCostEngine.NoteReferenceCost or ProductionCostEngine.NoteNotFrozen
                     or ProductionCostEngine.NoteNoOverhead or ProductionCostEngine.NotePolicyNotApproved
                     or ProductionCostEngine.NoteFinalReleaseNegative)))
        {
            var hours = n.Code is ProductionCostEngine.NoteStandardRate or ProductionCostEngine.NoteUnvaluedLabour
                or ProductionCostEngine.NoteOvertimeWithoutPremium;
            gaps.Add(new($"note_{n.Code}", SeverityProvisional, hours ? Labour : null,
                hours && !labourHoursShown ? null : n.Value is decimal v ? Unit(v) : null));
        }
        foreach (var f in releases.Flags) gaps.Add(new(f, SeverityReview, Value: f == FlagUnreleasedOutput || f == FlagOverReleased ? releases.UnreleasedAcceptedQty : null));
        if (late.Count(l => l.Treatment == TreatmentUnclassified) is var unclassified and > 0) gaps.Add(new("late_unclassified", SeverityReview, Value: unclassified));
        if (late.Count(l => l.Flags.Contains(FlagDuplicateSource)) is var duplicate and > 0) gaps.Add(new("late_duplicate_source", SeverityReview, Value: duplicate));
        if (late.Count(l => l.Flags.Contains(FlagOverlapsOriginal)) is var overlap and > 0) gaps.Add(new("late_overlaps_original", SeverityReview, Value: overlap));
        foreach (var c in checks.Where(c => c.Result == Fail)) gaps.Add(new("check_failed", SeverityIntegrity, Detail: c.Code));
        return gaps;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
