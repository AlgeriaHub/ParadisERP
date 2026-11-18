using System.Text.Json;
using ParadisERP.Application.Manufacturing;
using ParadisERP.Application.Regional;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Payroll;
using ParadisERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Engine = ParadisERP.Application.Payroll;

namespace ParadisERP.Infrastructure.Manufacturing;

/// <summary>
/// What an hour of an employee's payroll month costs, from payroll itself (costing plan section 3):
/// no rate is stored or typed anywhere, and nothing in payroll, attendance or the ledger is written.
///
/// <list type="bullet">
/// <item><b>Actual</b>, once every run holding the employee for the month is approved or paid: the
/// employer cost of their payslip lines — gross, employer social contribution and their share of the
/// run's employer levies — over the hours those lines paid: days present × the run's hours per day,
/// plus the overtime hours on the payslip. Paid leave is in the cost and not in the hours, so it is
/// spread over the hours worked (Revision 1, D12). Lines from every branch's run for the month count
/// together, since an employee who changed branch is paid twice in one month (costing plan F7).</item>
/// <item><b>Standard</b> before that: a full month of the employee as the payroll engine computes it
/// today — full attendance, no overtime, their allowances, statutory deductions off as on new runs —
/// over working days × hours per day.</item>
/// </list>
///
/// <para>
/// A run's employer levies are stored as a total; each line's share is its part of the run's social
/// base (the elements the run counted in it). That equals the engine's own figure whenever the base
/// is not clamped, which the Algeria preset never does (no floor, no ceiling).
/// </para>
/// <para>
/// C-05 (2026-11-09): an actual rate also carries the month's <b>overtime premium</b> — on each payslip overtime line, the
/// amount above the normal rate, amount × (multiplier − 1) ÷ multiplier, the multiplier read from the run's own settings
/// snapshot by the line's key (a key the snapshot no longer knows gives no premium: its pay counts as ordinary hours), plus
/// that part's share of the line's employer social and levies when the line is in the social base — the overtime hours
/// paid, and the hours bookings marked overtime in the month on every order and row. The engine uses them only under
/// "premium to overtime hours" (<see cref="ProductionCostEngine.HourRatesOf"/>).
/// </para>
/// </summary>
public sealed class LabourRates(PosDbContext db, PayrollSettingsStore payrollSettings, IRegionalSettingsProvider regional)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string ReasonNotApproved = "payroll_not_approved";
    public const string ReasonNoPaidHours = "no_paid_hours";
    public const string ReasonNoBaseRate = "no_base_rate";
    public const string ReasonSettingsRefused = "payroll_settings_incomplete";
    public const string ReasonUnknownEmployee = "unknown_employee";

    /// <summary>Rates already read in this request (the service is scoped): a report reads many orders of the same people.</summary>
    private readonly Dictionary<(Guid, int, int), LabourRate> known = [];
    private readonly Dictionary<Guid, LabourRate> knownStandard = [];

    public async Task<Dictionary<(Guid EmployeeId, int Year, int Month), LabourRate>> ForAsync(
        IEnumerable<(Guid EmployeeId, int Year, int Month)> keys, CancellationToken ct)
    {
        var asked = keys.Distinct().ToList();
        var result = asked.Where(known.ContainsKey).ToDictionary(k => k, k => known[k]);
        var wanted = asked.Where(k => !known.ContainsKey(k)).ToList();
        if (wanted.Count == 0) return result;

        var employees = wanted.Select(k => k.EmployeeId).Distinct().ToList();
        var periods = wanted.Select(k => k.Year * 100 + k.Month).Distinct().ToList();
        var lines = await db.PayrollRunLines.AsNoTracking()
            .Where(l => employees.Contains(l.EmployeeId) && l.Run!.Status != PayrollRunStatus.Reversed
                && periods.Contains(l.Run.PeriodYear * 100 + l.Run.PeriodMonth))
            .Select(l => new PaidLine(l.Id, l.RunId, l.EmployeeId, l.Gross, l.SocialEmployer, l.DaysPresent, l.AttendanceSource))
            .ToListAsync(ct);
        var runIds = lines.Select(l => l.RunId).Distinct().ToList();
        var runs = await db.PayrollRuns.AsNoTracking().Where(r => runIds.Contains(r.Id))
            .Select(r => new PaidRun(r.Id, r.PeriodYear, r.PeriodMonth, r.Status, r.EmployerLevies, r.SettingsSnapshotJson))
            .ToDictionaryAsync(r => r.Id, ct);

        var lineIds = lines.Select(l => l.Id).ToList();
        var overtime = await db.PayrollRunLineElements.AsNoTracking()
            .Where(e => lineIds.Contains(e.LineId) && e.Kind == PayrollElementKind.Overtime)
            .Select(e => new { e.LineId, e.Key, e.Qty, e.Amount, e.InSocialBase }).ToListAsync(ct);
        // The social base of each line that paid overtime in it, to share the line's employer charges with its premium.
        var premiumLines = overtime.Where(o => o.InSocialBase && o.Amount != 0m).Select(o => o.LineId).Distinct().ToList();
        var lineSocialBase = premiumLines.Count == 0 ? [] : (await db.PayrollRunLineElements.AsNoTracking()
                .Where(e => premiumLines.Contains(e.LineId) && e.InSocialBase)
                .Select(e => new { e.LineId, e.Amount }).ToListAsync(ct))
            .GroupBy(e => e.LineId).ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));
        // Every booking of these employee-months marked overtime, on every order and row (C-05).
        var marked = (await db.LaborBookings.AsNoTracking()
                .Where(b => employees.Contains(b.EmployeeId) && periods.Contains(b.PayrollYear * 100 + b.PayrollMonth) && b.OvertimeHours > 0m)
                .Select(b => new { b.EmployeeId, b.PayrollYear, b.PayrollMonth, b.OvertimeHours }).ToListAsync(ct))
            .GroupBy(b => (b.EmployeeId, b.PayrollYear, b.PayrollMonth)).ToDictionary(g => g.Key, g => g.Sum(b => b.OvertimeHours));

        // The levy base of every line of the runs that carry a levy, to split each run's total.
        var levied = runs.Values.Where(r => r.EmployerLevies != 0m).Select(r => r.Id).ToList();
        var socialBase = levied.Count == 0 ? [] : (await db.PayrollRunLineElements.AsNoTracking()
                .Where(e => levied.Contains(e.Line!.RunId) && e.InSocialBase)
                .Select(e => new { e.LineId, e.Line!.RunId, e.Amount }).ToListAsync(ct))
            .GroupBy(e => (e.RunId, e.LineId)).ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));
        var runBase = socialBase.GroupBy(kv => kv.Key.RunId).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));

        var settingsOf = new Dictionary<Guid, Engine.PayrollSettings>();
        Engine.PayrollSettings? current = null;
        async Task<Engine.PayrollSettings> SettingsAsync(PaidRun run)
        {
            if (settingsOf.TryGetValue(run.Id, out var known)) return known;
            var snapshot = string.IsNullOrWhiteSpace(run.SettingsSnapshotJson) ? null
                : JsonSerializer.Deserialize<Engine.PayrollSettings>(run.SettingsSnapshotJson, Json);
            return settingsOf[run.Id] = snapshot ?? (current ??= (await payrollSettings.ReadAsync(ct)).Settings);
        }

        var needStandard = new List<(Guid EmployeeId, int Year, int Month)>();
        foreach (var key in wanted)
        {
            var mine = lines.Where(l => l.EmployeeId == key.EmployeeId
                && runs[l.RunId].Year == key.Year && runs[l.RunId].Month == key.Month).ToList();
            if (mine.Count == 0 || mine.Any(l => runs[l.RunId].Status is PayrollRunStatus.Draft or PayrollRunStatus.Locked))
            {
                needStandard.Add(key);
                continue;
            }

            decimal cost = 0m, hours = 0m, premium = 0m, overtimeHours = 0m;
            foreach (var line in mine)
            {
                var run = runs[line.RunId];
                var settings = await SettingsAsync(run);
                var levyShare = run.EmployerLevies != 0m && runBase.GetValueOrDefault(run.Id) is var total and not 0m
                    ? run.EmployerLevies * socialBase.GetValueOrDefault((run.Id, line.Id)) / total
                    : 0m;
                cost += line.Gross + line.SocialEmployer + levyShare;
                // A monthly employee nobody entered attendance for is paid the full month (PayrollWarnings.MonthlyWithoutAttendance).
                var days = line.AttendanceSource == AttendanceSource.None ? settings.WorkingDaysPerMonth : line.DaysPresent;
                var lineOvertime = overtime.Where(o => o.LineId == line.Id).ToList();
                var lineOvertimeHours = lineOvertime.Sum(o => OvertimeHours(settings, o.Key, o.Qty));
                hours += days * settings.HoursPerDay + lineOvertimeHours;
                overtimeHours += lineOvertimeHours;
                foreach (var o in lineOvertime)
                {
                    var gross = Premium(settings, o.Key, o.Amount);
                    if (gross == 0m) continue;
                    // The employer charges follow the pay they are computed on: the premium's share of the line's social base.
                    var charges = o.InSocialBase && lineSocialBase.GetValueOrDefault(line.Id) is var lineBase and > 0m
                        ? (line.SocialEmployer + levyShare) * gross / lineBase
                        : 0m;
                    premium += gross + charges;
                }
            }
            var withoutSocial = mine.All(l => l.SocialEmployer == 0m);
            result[key] = hours > 0
                ? new LabourRate(cost / hours, LabourRateKind.Actual, null, cost, hours, withoutSocial,
                    OvertimePremium: premium, PaidOvertimeHours: overtimeHours, MarkedOvertimeHours: marked.GetValueOrDefault(key))
                : LabourRate.Missing(ReasonNoPaidHours);
        }

        if (needStandard.Count > 0)
        {
            var standard = await StandardAsync(needStandard.Select(k => k.EmployeeId), ct);
            foreach (var key in needStandard)
            {
                var rate = standard[key.EmployeeId];
                // A standard rate is a month without overtime: it has no premium to spread, whatever the bookings marked.
                result[key] = (rate.Kind == LabourRateKind.Standard ? rate with { Reason = ReasonNotApproved } : rate)
                    with { MarkedOvertimeHours = marked.GetValueOrDefault(key) };
            }
        }
        foreach (var key in wanted) known[key] = result[key];
        return result;
    }

    /// <summary>
    /// Each employee's standard rate today. Used for bookings whose month is not approved yet and for
    /// the order's estimate (the average of its line's crew).
    /// </summary>
    public async Task<Dictionary<Guid, LabourRate>> StandardAsync(IEnumerable<Guid> employeeIds, CancellationToken ct)
    {
        var asked = employeeIds.Distinct().ToList();
        var result = asked.Where(knownStandard.ContainsKey).ToDictionary(id => id, id => knownStandard[id]);
        var ids = asked.Where(id => !knownStandard.ContainsKey(id)).ToList();
        if (ids.Count == 0) return result;

        var people = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        var allowances = (await db.EmployeeAllowances.AsNoTracking().Where(a => ids.Contains(a.EmployeeId)).ToListAsync(ct))
            .GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());
        var settings = (await payrollSettings.ReadAsync(ct)).Settings;
        var region = regional.Get();
        var paidHours = settings.WorkingDaysPerMonth * settings.HoursPerDay;

        foreach (var id in ids)
        {
            if (!people.TryGetValue(id, out var employee)) { result[id] = LabourRate.Missing(ReasonUnknownEmployee); continue; }
            if (employee.BaseRate <= 0m || paidHours <= 0m) { result[id] = LabourRate.Missing(ReasonNoBaseRate); continue; }
            var input = new Engine.EmployeeInput(
                employee.Id, (Engine.PayBasis)(int)employee.PayBasis, employee.BaseRate,
                new Engine.AttendanceSummary(settings.WorkingDaysPerMonth, 0m, 0m, new Dictionary<string, decimal>(), Engine.AttendanceSource.Declared),
                allowances.GetValueOrDefault(id)?.Select(a => new Engine.EmployeeAllowanceOverride(a.AllowanceKey, a.Value, a.IsEnabled)).ToList());
            try
            {
                var month = Engine.PayrollEngine.ComputeOne(settings, region, input, calculateStatutoryDeductions: false);
                var cost = month.Gross + month.SocialEmployer + month.EmployerLevies;
                // Statutory deductions off, as on new runs: no employer social contribution in it, and it says so.
                result[id] = cost > 0m
                    ? new LabourRate(cost / paidHours, LabourRateKind.Standard, WithoutEmployerSocial: month.SocialEmployer == 0m)
                    : LabourRate.Missing(ReasonNoBaseRate);
            }
            catch (Engine.PayrollRefusedException)
            {
                result[id] = LabourRate.Missing(ReasonSettingsRefused);
            }
        }
        foreach (var id in ids) knownStandard[id] = result[id];
        return result;
    }

    /// <summary>A payslip's overtime quantity in hours: a band counted in days is that many full days.</summary>
    private static decimal OvertimeHours(Engine.PayrollSettings settings, string key, decimal? qty)
    {
        var rule = settings.OvertimeRules.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal));
        var quantity = qty ?? 0m;
        return rule?.Unit == Engine.OvertimeUnit.Days ? quantity * settings.HoursPerDay : quantity;
    }

    /// <summary>
    /// The part of an overtime line's amount above the normal rate: amount × (multiplier − 1) ÷ multiplier, the multiplier
    /// read from the run's settings by the line's key. A key the settings do not know, or a multiplier of 1, gives none.
    /// </summary>
    private static decimal Premium(Engine.PayrollSettings settings, string key, decimal amount)
    {
        var rule = settings.OvertimeRules.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal));
        return rule is { Multiplier: > 1m } ? amount * (rule.Multiplier - 1m) / rule.Multiplier : 0m;
    }

    private sealed record PaidLine(Guid Id, Guid RunId, Guid EmployeeId, decimal Gross, decimal SocialEmployer, decimal DaysPresent, AttendanceSource AttendanceSource);

    private sealed record PaidRun(Guid Id, int Year, int Month, PayrollRunStatus Status, decimal EmployerLevies, string? SettingsSnapshotJson);
}
