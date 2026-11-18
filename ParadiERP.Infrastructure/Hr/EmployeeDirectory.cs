using ParadiERP.Application.Contracts;
using ParadiERP.Application.Integrations;
using ParadiERP.Application.Regional;
using ParadiERP.Domain;
using ParadiERP.Infrastructure.Catalog;
using ParadiERP.Infrastructure.Payroll;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Engine = ParadiERP.Application.Payroll;

namespace ParadiERP.Infrastructure.Hr;

/// <summary>
/// The people the company pays: who they are, what they are paid on, and when they left.
///
/// <para>
/// <b>There is no delete.</b> A person who has been paid is named by payslips, advances and
/// postings that must go on naming them years later, so leaving is a date and every foreign key
/// pointing at an employee is Restrict. The list filters them out; history does not.
/// </para>
/// <para>
/// A service rather than a handful of endpoint lambdas because three rules are worth testing
/// without a web server: the number is minted inside the write scope, an allowance override can
/// only name an allowance the company has actually defined, and an employee's capture mode is
/// resolved in exactly one place.
/// </para>
/// </summary>
public sealed class EmployeeDirectory(
    PosDbContext db, PayrollSettingsStore settings, IRegionalSettingsProvider regional, TimeProvider time)
{
    /// <summary>
    /// Who this branch pays this month: active, hired by the end of it, and not gone before it
    /// began. The sheet opens with this query and the run repeats it — an employee who moved
    /// branch mid-month is paid whole by the branch holding them now (project memory,
    /// 2026-09-24), which is why this reads the current <c>BranchId</c> and no history.
    /// </summary>
    public IQueryable<Employee> Payable(Guid branchId, AttendancePeriod period) =>
        db.Employees.AsNoTracking()
            .Where(e => e.BranchId == branchId)
            .Where(e => e.HiredOn <= period.LastDay)
            // Somebody who left on the 20th is paid for that month and for no month after it.
            // Leaving also clears IsActive, so testing the flag alone would drop the last
            // payslip of everybody who ever leaves — which is the one nobody notices is missing
            // until the person asks for it. A leaving date is the answer where there is one;
            // the flag is what it means for everybody else, where it reads "suspended".
            .Where(e => e.LeftOn == null ? e.IsActive : e.LeftOn >= period.FirstDay);

    /// <summary>The same list, ordered and read. The shape the payroll run asks for.</summary>
    public Task<List<Employee>> PayableAsync(Guid branchId, AttendancePeriod period, CancellationToken ct = default) =>
        Payable(branchId, period).OrderBy(e => e.Number).ToListAsync(ct);

    /// <summary>Whether this person was on the payroll for any part of that month.</summary>
    public static bool CoversPeriod(Employee e, AttendancePeriod period) =>
        e.HiredOn <= period.LastDay && (e.LeftOn is null || e.LeftOn >= period.FirstDay);

    // ---------- reading ----------

    /// <param name="active">
    /// True is "still with us": active and with no leaving date. False is everyone else — the
    /// suspended and the departed — because a screen that offers "inactive" and then hides the
    /// people who left would be hiding the only ones anybody looks for.
    /// </param>
    public async Task<EmployeePageDto> ListAsync(
        Guid? branchId, bool? active, string? q, int skip, int take, CancellationToken ct = default)
    {
        var query = db.Employees.AsNoTracking();
        if (branchId is Guid branch) query = query.Where(e => e.BranchId == branch);
        if (active == true) query = query.Where(e => e.IsActive && e.LeftOn == null);
        else if (active == false) query = query.Where(e => !e.IsActive || e.LeftOn != null);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            var fingerprint = FingerprintNumbers.Normalize(term);
            query = query.Where(e =>
                e.Number.Contains(term) || e.FullName.Contains(term) ||
                (e.JobTitle != null && e.JobTitle.Contains(term)) ||
                (e.Phone != null && e.Phone.Contains(term)) ||
                (e.NationalId != null && e.NationalId.Contains(term)) ||
                (fingerprint != null && e.FingerprintNumber == fingerprint));
        }

        var total = await query.CountAsync(ct);
        var page = await query
            .OrderBy(e => e.Number)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 1000))
            .ToListAsync(ct);

        return new EmployeePageDto(
            total, Math.Max(0, skip), Math.Clamp(take, 1, 1000), await MapAsync(page, ct));
    }

    public async Task<EmployeeDto?> ReadAsync(Guid id, CancellationToken ct = default)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (employee is null) return null;

        return (await MapAsync([employee], ct))[0];
    }

    // ---------- writing ----------

    /// <param name="viewerBranchId">
    /// The caller's branch when they are branch-locked, null for company scope: a refusal never
    /// names another branch's employee to someone who may not read their file (P0.1).
    /// </param>
    public async Task<HrResult<EmployeeDto>> CreateAsync(
        UpsertEmployeeRequest r, CancellationToken ct = default, Guid? viewerBranchId = null)
    {
        ArgumentNullException.ThrowIfNull(r);

        var profile = (await settings.ReadAsync(ct)).Settings;
        if (Validate(r, profile) is { } problem) return problem;
        // Nobody is hired onto Daily any more; nobody was on it before, so there is nothing to keep.
        var (basis, retired) = ResolveBasis(r.PayBasis, current: null);
        if (retired is not null) return retired;
        if (!await db.Branches.AnyAsync(b => b.Id == r.BranchId, ct))
            return HrResult<EmployeeDto>.Invalid(HrProblems.BranchUnknown, "That branch does not exist.");

        await using var scope = await db.BeginWriteScopeAsync(ct);
        if (await FingerprintHolderAsync(r.FingerprintNumber, null, viewerBranchId, ct) is { } holder) return holder;

        var createdAt = time.GetUtcNow();
        var employee = new Employee { CreatedAtUtc = createdAt.UtcDateTime };
        Apply(employee, r, basis);

        // The number (owner, 2026-10-05): Emp- + the employee's branch code + the company's YYMM +
        // the next number of that branch and month, e.g. Emp-112610001 (CodeSeries). Drawn inside the
        // write scope, as the old EMP-000001 count was: the scope serialises writers, which is what
        // keeps two employees created in the same second from sharing one. It never changes, even
        // when the employee later moves branch; people numbered EMP-000001… before keep theirs.
        var zone = AttendancePeriod.Zone(regional.Get().TimeZone);
        employee.Number = await CodeSeriesDraw.EmployeeAsync(db, employee.BranchId, createdAt, zone, ct);
        db.Employees.Add(employee);

        ReplaceAllowances(employee.Id, [], r.Allowances);

        // The unique index is the backstop for a number written outside the lock: drawn again,
        // never a 500 for the clerk. EF Core rolls a failed save back to its own savepoint, so the
        // scope stays usable.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateException) when (attempt < 4)
            {
                if (!await CodeSeriesDraw.EmployeeTakenAsync(db, employee.Number, ct)) throw;
                employee.Number = await CodeSeriesDraw.EmployeeAsync(db, employee.BranchId, createdAt, zone, ct);
            }
        }
        await scope.CommitAsync(ct);

        return HrResult<EmployeeDto>.Success((await MapAsync([employee], ct))[0]);
    }

    /// <param name="viewerBranchId">As on <see cref="CreateAsync"/>.</param>
    public async Task<HrResult<EmployeeDto>> UpdateAsync(
        Guid id, UpsertEmployeeRequest r, CancellationToken ct = default, Guid? viewerBranchId = null)
    {
        ArgumentNullException.ThrowIfNull(r);

        var profile = (await settings.ReadAsync(ct)).Settings;
        if (Validate(r, profile) is { } problem) return problem;
        if (!await db.Branches.AnyAsync(b => b.Id == r.BranchId, ct))
            return HrResult<EmployeeDto>.Invalid(HrProblems.BranchUnknown, "That branch does not exist.");

        await using var scope = await db.BeginWriteScopeAsync(ct);

        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (employee is null)
            return HrResult<EmployeeDto>.NotFound(HrProblems.EmployeeNotFound, "No such employee.");
        if (await FingerprintHolderAsync(r.FingerprintNumber, id, viewerBranchId, ct) is { } holder) return holder;
        // Decided against the row as it stands: a legacy Daily employee may stay Daily or move to
        // Monthly, a Monthly one may not become Daily, and nobody is converted by this save unasked.
        var (basis, retired) = ResolveBasis(r.PayBasis, employee.PayBasis);
        if (retired is not null) return retired;

        Apply(employee, r, basis);
        employee.UpdatedAtUtc = DateTime.UtcNow;

        var current = await db.EmployeeAllowances.Where(a => a.EmployeeId == id).ToListAsync(ct);
        ReplaceAllowances(id, current, r.Allowances);

        // Their account follows them: renamed with them, switched off when they are switched off.
        if (await SaveFollowingAccountsAsync(employee, ct) is { } refused) return refused;
        await scope.CommitAsync(ct);

        return HrResult<EmployeeDto>.Success((await MapAsync([employee], ct))[0]);
    }

    /// <summary>
    /// Records that somebody has left: the run stops picking them up from the month after, and
    /// everything that already names them is untouched. Their own route rather than a field on
    /// the form, because it is the one employee edit with a consequence.
    /// </summary>
    public async Task<HrResult<EmployeeDto>> SetLeftAsync(
        Guid id, EmployeeLeaveRequest r, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(r);

        await using var scope = await db.BeginWriteScopeAsync(ct);

        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (employee is null)
            return HrResult<EmployeeDto>.NotFound(HrProblems.EmployeeNotFound, "No such employee.");
        if (r.LeftOn < employee.HiredOn)
            return HrResult<EmployeeDto>.Invalid(
                HrProblems.LeftBeforeHired, "A leaving date cannot fall before the hire date.");

        employee.LeftOn = r.LeftOn;
        // Left and inactive are two facts, and this route sets both: somebody who has gone is not
        // also waiting to be picked up by next month's run. Reinstating is the edit form, which
        // can clear both.
        employee.IsActive = false;
        employee.Notes = Append(employee.Notes, r.Note);
        employee.UpdatedAtUtc = DateTime.UtcNow;

        // Somebody who has gone signs in no more: their account is switched off, a cashier's till too.
        if (await SaveFollowingAccountsAsync(employee, ct) is { } refused) return refused;
        await scope.CommitAsync(ct);

        return HrResult<EmployeeDto>.Success((await MapAsync([employee], ct))[0]);
    }

    /// <summary>
    /// Saves the employee together with what their accounts owe them (EmployeeAccounts): the name,
    /// and access switched off for someone who has left or is switched off. Accounts are saved with
    /// versions, which revokes their open sessions. Null when saved; the refusal otherwise, before
    /// anything is written.
    /// </summary>
    private async Task<HrResult<EmployeeDto>?> SaveFollowingAccountsAsync(Employee employee, CancellationToken ct)
    {
        var (changed, code, message) = await ParadiERP.Infrastructure.Auth.EmployeeAccounts.FollowEmployeeAsync(db, employee, ct);
        if (code is not null) return HrResult<EmployeeDto>.Invalid(code, message!);
        if (changed.Count > 0) await db.SaveWithVersionsAsync(ct, changed.Cast<SyncEntity>().ToArray());
        else await db.SaveChangesAsync(ct);
        return null;
    }

    // ---------- rules ----------

    /// <summary>
    /// The refusal when somebody else — still employed or long gone — is enrolled under that
    /// number, naming them by their employee number so the clerk knows whose card to correct.
    /// Asked inside the write scope; the unique index is the backstop.
    /// <para>
    /// A branch-locked caller (<paramref name="viewerBranchId"/>) is told only that the holder is at
    /// another branch, never who (P0.1): the number is unique company-wide, so the refusal itself
    /// cannot be avoided, but naming the person would hand over a file the caller may not open.
    /// </para>
    /// </summary>
    private async Task<HrResult<EmployeeDto>?> FingerprintHolderAsync(
        string? raw, Guid? self, Guid? viewerBranchId, CancellationToken ct)
    {
        if (FingerprintNumbers.Normalize(raw) is not { } number) return null;
        var holder = await db.Employees.AsNoTracking()
            .Where(e => e.FingerprintNumber == number && e.Id != self)
            .Select(e => new { e.Number, e.BranchId })
            .FirstOrDefaultAsync(ct);
        if (holder is null) return null;
        if (viewerBranchId is Guid viewer && holder.BranchId != viewer)
            return HrResult<EmployeeDto>.Conflict(
                HrProblems.FingerprintTakenElsewhere,
                $"Fingerprint number {number} is already enrolled for an employee of another branch.");
        return HrResult<EmployeeDto>.Taken(
            HrProblems.FingerprintTaken, $"Fingerprint number {number} is already {holder.Number}'s.", holder.Number);
    }

    private static HrResult<EmployeeDto>? Validate(UpsertEmployeeRequest r, Engine.PayrollSettings profile)
    {
        var names = Names(r);
        if (names is null)
            return HrResult<EmployeeDto>.Invalid(HrProblems.NameRequired, "An employee needs a first name and a family name.");
        if (names.Value.Family is null)
            return HrResult<EmployeeDto>.Invalid(HrProblems.NameRequired, "An employee needs a name.");
        if (r.BranchId == Guid.Empty)
            return HrResult<EmployeeDto>.Invalid(HrProblems.BranchRequired, "An employee belongs to a branch.");
        // Blank is not unknown: it means Monthly on a create and "as it is" on an edit (ResolveBasis).
        if (!string.IsNullOrWhiteSpace(r.PayBasis) && ParseBasis(r.PayBasis) is null)
            return HrResult<EmployeeDto>.Invalid(HrProblems.PayBasisUnknown, "Pay basis is Monthly.");
        if (ParsePayout(r.PayoutMethod) is null)
            return HrResult<EmployeeDto>.Invalid(HrProblems.PayoutMethodUnknown, "Payout method is Cash or Bank.");
        if (r.AttendanceModeOverride is not null && ParseMode(r.AttendanceModeOverride) is null)
            return HrResult<EmployeeDto>.Invalid(
                HrProblems.AttendanceModeUnknown, "Attendance mode is Recorded or Declared.");

        // A negative rate would be a wage the run pays backwards, and nothing downstream questions
        // a figure that arrived as data.
        if (r.BaseRate < 0)
            return HrResult<EmployeeDto>.Invalid(HrProblems.BaseRateNegative, "A pay rate cannot be negative.");
        if (r.LeftOn is DateOnly left && left < r.HiredOn)
            return HrResult<EmployeeDto>.Invalid(
                HrProblems.LeftBeforeHired, "A leaving date cannot fall before the hire date.");
        // Refused rather than cut: a shortened number could be somebody else's.
        if (FingerprintNumbers.Normalize(r.FingerprintNumber) is { Length: > FingerprintNumbers.MaxLength })
            return HrResult<EmployeeDto>.Invalid(
                HrProblems.FingerprintTooLong,
                $"A fingerprint number is at most {FingerprintNumbers.MaxLength} characters.");
        // The receipt channel's own test of an address (IntegrationSettingsValidator), not a second
        // one; too long is refused rather than cut, like the fingerprint number: a shortened address
        // is somebody else's or nobody's. Blank is no address, never a refusal.
        if (r.Email?.Trim() is { Length: > 0 } email
            && (email.Length > EmailMaxLength || !IntegrationSettingsValidator.IsEmail(email)))
            return HrResult<EmployeeDto>.Invalid(HrProblems.EmailInvalid, EmailInvalidMessage);

        if (r.Allowances is null) return null;

        var defined = profile.Allowances.Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var allowance in r.Allowances)
        {
            var key = (allowance.AllowanceKey ?? "").Trim().ToLowerInvariant();
            // Refused rather than dropped: an override saved against a key nobody defined is an
            // allowance the admin believes they set and no payslip will ever show.
            if (!defined.Contains(key))
                return HrResult<EmployeeDto>.Invalid(
                    HrProblems.AllowanceUnknown, "That allowance is not defined in the payroll settings.", key);
            if (!seen.Add(key))
                return HrResult<EmployeeDto>.Invalid(
                    HrProblems.AllowanceDuplicate, "One value per allowance per person.", key);
            if (allowance.Value < 0)
                return HrResult<EmployeeDto>.Invalid(
                    HrProblems.AllowanceNegative, "An allowance cannot be negative.", key);
        }
        return null;
    }

    /// <summary>
    /// The pay basis a save leaves the person on (owner, 2026-10-05, confirming answers 41/47 of
    /// 2026-10-02: "there are no daily workers on payroll; a daily worker's wage is recorded under
    /// expenses, item Daily labour" — the payment-voucher category <c>dailyLabour</c>). Daily is
    /// refused for anyone not already on it. A legacy Daily row — production may hold some — keeps
    /// computing exactly as it did (the engine and the enum are untouched) until someone moves it to
    /// Monthly by hand: no save and no migration converts it. Blank means Monthly on a create and the
    /// stored basis on an edit, so an edit that does not name the basis cannot change it either way.
    /// </summary>
    private static (PayBasis Basis, HrResult<EmployeeDto>? Refusal) ResolveBasis(string? raw, PayBasis? current)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (current ?? PayBasis.Monthly, null);
        // Validate has already refused a basis that is not one; this is a known value.
        var asked = ParseBasis(raw)!.Value;
        if (asked == PayBasis.Daily && current != PayBasis.Daily)
            return (asked, HrResult<EmployeeDto>.Invalid(HrProblems.PayBasisDailyRetired, DailyRetiredMessage));
        return (asked, null);
    }

    public const string DailyRetiredMessage =
        "There are no daily workers on payroll: pay a day worker with a payment voucher under Daily labour.";

    public const string EmailInvalidMessage = "Enter a valid email address, or leave it empty.";

    private const int EmailMaxLength = 200;

    /// <summary>
    /// The name a save files (owner, PinSay #301: first name and family name apart, printed first name
    /// first). Sent in parts, both are required and the full name is composed from them; sent whole by
    /// an older client, it is filed as the family name with no first name — what the migration did with
    /// everyone already on file. Null when parts were sent and one of them is blank.
    /// </summary>
    private static (string? First, string? Family, string Full)? Names(UpsertEmployeeRequest r)
    {
        if (r.FirstName is null && r.FamilyName is null)
        {
            var whole = Trim(r.FullName, 200);
            return (null, whole, whole ?? "");
        }
        var first = Trim(r.FirstName, 100);
        var family = Trim(r.FamilyName, 200);
        if (first is null || family is null) return null;
        return (first, family, Shorten($"{first} {family}", 200));
    }

    private static void Apply(Employee employee, UpsertEmployeeRequest r, PayBasis basis)
    {
        // Validate has refused a request whose names do not resolve.
        var (first, family, full) = Names(r)!.Value;
        employee.FirstName = first;
        employee.FamilyName = family;
        employee.FullName = full;
        employee.NationalId = Trim(r.NationalId, 40);
        employee.SocialSecurityNumber = Trim(r.SocialSecurityNumber, 40);
        employee.FingerprintNumber = FingerprintNumbers.Normalize(r.FingerprintNumber);
        employee.BirthDate = r.BirthDate;
        employee.Phone = Trim(r.Phone, 60);
        // Null leaves the stored address alone, as Allowances does, so a form that predates the field
        // cannot wipe it; blank clears it. Validate has already refused one that is too long.
        if (r.Email is not null) employee.Email = Trim(r.Email, EmailMaxLength);
        employee.Address = Trim(r.Address, 400);
        employee.BranchId = r.BranchId;
        employee.JobTitle = Trim(r.JobTitle, 120);
        employee.HiredOn = r.HiredOn;
        employee.LeftOn = r.LeftOn;
        employee.PayBasis = basis;
        employee.BaseRate = r.BaseRate;
        employee.PayoutMethod = ParsePayout(r.PayoutMethod)!.Value;
        employee.BankAccount = Trim(r.BankAccount, 60);
        employee.AttendanceModeOverride = r.AttendanceModeOverride is null ? null : ParseMode(r.AttendanceModeOverride);
        employee.IsActive = r.IsActive;
        employee.Notes = Trim(r.Notes, 500);
    }

    /// <summary>
    /// The override rows this person should have. Null means the form does not edit allowances
    /// and leaves the stored ones alone; a list means <i>this</i> is the set, so a row left out
    /// is a row removed.
    /// </summary>
    private void ReplaceAllowances(
        Guid employeeId, List<EmployeeAllowance> current, IReadOnlyList<EmployeeAllowanceDto>? wanted)
    {
        if (wanted is null) return;

        db.EmployeeAllowances.RemoveRange(current);
        foreach (var allowance in wanted)
            db.EmployeeAllowances.Add(new EmployeeAllowance
            {
                EmployeeId = employeeId,
                AllowanceKey = (allowance.AllowanceKey ?? "").Trim().ToLowerInvariant(),
                Value = allowance.Value,
                IsEnabled = allowance.IsEnabled
            });
    }

    // ---------- shaping ----------

    private async Task<Dictionary<Guid, List<EmployeeAllowanceDto>>> AllowancesOfAsync(
        IReadOnlyList<Guid> employeeIds, CancellationToken ct)
    {
        if (employeeIds.Count == 0) return [];

        var rows = await db.EmployeeAllowances.AsNoTracking()
            .Where(a => employeeIds.Contains(a.EmployeeId))
            .ToListAsync(ct);

        return rows
            .GroupBy(a => a.EmployeeId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(a => a.AllowanceKey, StringComparer.Ordinal)
                      .Select(a => new EmployeeAllowanceDto(a.AllowanceKey, a.Value, a.IsEnabled))
                      .ToList());
    }

    /// <summary>
    /// Employees as the screens read them: with the branch's name, their allowance overrides,
    /// the capture mode actually in force, and what they still owe against advances.
    ///
    /// <para>
    /// <c>OpenAdvances</c> is summed <b>in memory</b>: SQLite stores decimal as TEXT, so a
    /// server-side Sum would be adding strings. Read here rather than left at zero because the
    /// field is on the wire — a figure a screen shows and nobody fills is worse than no field.
    /// </para>
    /// </summary>
    private async Task<List<EmployeeDto>> MapAsync(IReadOnlyList<Employee> staff, CancellationToken ct)
    {
        if (staff.Count == 0) return [];

        var ids = staff.Select(e => e.Id).Distinct().ToList();
        var branchIds = staff.Select(e => e.BranchId).Distinct().ToList();

        var branches = await db.Branches.AsNoTracking()
            .Where(b => branchIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.NameFr, ct);

        var allowances = await AllowancesOfAsync(ids, ct);

        var advances = await db.WageAdvances.AsNoTracking()
            .Where(a => ids.Contains(a.EmployeeId) && a.Status == WageAdvanceStatus.Open)
            .Select(a => new { a.EmployeeId, a.Amount, a.Recovered })
            .ToListAsync(ct);
        var owed = advances
            .GroupBy(a => a.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Sum(a => Math.Max(0m, a.Amount - a.Recovered)));

        var companyDefault = (await settings.ReadAsync(ct)).Settings.DefaultAttendanceMode;

        var accounts = (await db.Users.AsNoTracking()
                .Where(u => u.EmployeeId != null && ids.Contains(u.EmployeeId!.Value) && u.IsActive && !u.IsDeleted)
                .Select(u => new { EmployeeId = u.EmployeeId!.Value, u.Id, SignIn = u.LoginId ?? u.Username })
                .ToListAsync(ct))
            .GroupBy(a => a.EmployeeId)
            .ToDictionary(g => g.Key, g => g.First());

        return staff.Select(e => Map(
            e,
            branches.GetValueOrDefault(e.BranchId, ""),
            allowances.GetValueOrDefault(e.Id, []),
            owed.GetValueOrDefault(e.Id, 0m),
            companyDefault,
            accounts.TryGetValue(e.Id, out var account) ? (account.Id, account.SignIn) : null)).ToList();
    }

    private static EmployeeDto Map(
        Employee e, string branchName, IReadOnlyList<EmployeeAllowanceDto> allowances,
        decimal openAdvances, Engine.AttendanceMode companyDefault, (Guid Id, string SignIn)? account) =>
        new(e.Id, e.Number, e.FullName,
            e.BranchId, branchName,
            e.PayBasis.ToString(), e.BaseRate, e.PayoutMethod.ToString(),
            e.HiredOn, e.IsActive, e.LeftOn,
            e.NationalId, e.SocialSecurityNumber, e.BirthDate,
            e.Phone, e.Address, e.JobTitle, e.BankAccount,
            e.AttendanceModeOverride?.ToString(),
            // The mode in force, resolved once here so no screen has to redo it and get it wrong.
            AttendanceModes.Resolve(e.AttendanceModeOverride, companyDefault).ToString(),
            e.Notes, allowances, openAdvances, e.FingerprintNumber,
            account?.Id, account?.SignIn, e.Email, e.FirstName, e.FamilyName);

    /// <summary>
    /// A stamp this code stored as UTC, marked as UTC whatever the provider handed back. MySQL
    /// returns DATETIME as Unspecified, which serialises with no 'Z' and is then read by the
    /// browser as its own local time — hours out for anyone outside the company's zone.
    /// </summary>
    internal static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    internal static DateTime? Utc(DateTime? value) => value is null ? null : Utc(value.Value);

    // ---------- helpers ----------

    // Defined names only: Enum.TryParse also reads "0" as Daily and "7" as a basis nobody defined.
    private static PayBasis? ParseBasis(string? value) =>
        Enum.TryParse<PayBasis>(value?.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;

    private static PayoutMethod? ParsePayout(string? value) =>
        Enum.TryParse<PayoutMethod>(value?.Trim(), ignoreCase: true, out var parsed) ? parsed : null;

    private static AttendanceMode? ParseMode(string? value) =>
        Enum.TryParse<AttendanceMode>(value?.Trim(), ignoreCase: true, out var parsed) ? parsed : null;

    private static string? Append(string? notes, string? addition)
    {
        var extra = addition?.Trim();
        if (string.IsNullOrEmpty(extra)) return notes;
        return Trim(string.IsNullOrWhiteSpace(notes) ? extra : $"{notes}\n{extra}", 500);
    }

    private static string? Trim(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    private static string Shorten(string value, int max) => value.Length > max ? value[..max] : value;
}
