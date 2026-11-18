using System.Globalization;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Engine = AlgeriaPos.Application.Rentals;

namespace AlgeriaPos.Infrastructure.Receivables;

/// <summary>Who is acting on a voucher, as the token says.</summary>
/// <param name="UserId">The login's id (<c>sub</c>), stored on an approval.</param>
/// <param name="Username">The login name — what <see cref="PaymentVoucher.RequestedBy"/> holds.</param>
/// <param name="MayApproveOwn">
/// Holds <c>finance.vouchers.approve_own</c>: may approve a voucher they raised themselves (the Admin
/// role's baseline; the Manager role does not hold it). AUTH-1 — no longer "is Admin".
/// </param>
public sealed record VoucherActor(Guid? UserId, string? Username, bool MayApproveOwn);

/// <summary>
/// Codes beside the English sentence of a warning, so the screen can say it in the reader's
/// language — the payroll precedent (<c>PayrollProblems</c>).
/// </summary>
public static class PaymentVoucherWarnings
{
    /// <summary>Cash paid out of a box whose count is below the amount. Args: amount, balance, box.</summary>
    public const string BoxShort = "boxShort";
    /// <summary>A cash voucher at a branch with no open box: the books moved, no drawer did. Args: none.</summary>
    public const string NoCashBox = "noCashBox";
    /// <summary>Part of a refund was set against what the customer owes. Args: deducted, paid.</summary>
    public const string TakenOffDebt = "takenOffDebt";
}

/// <summary>
/// Money going out: the payment voucher, from the owner's answers 20–27 of 2026-09-30.
///
/// <para>
/// <b>The lifecycle is the rule</b> (answer 25 — "any amount paid out needs the manager's
/// approval"). Raised → <see cref="PaymentVoucherStatus.Pending"/>, which moves nothing and
/// posts nothing: it is paper. Approved by a manager or an administrator, who is recorded with
/// the hour. Paid, which is the one moment cash leaves the branch's box and the ledger moves.
/// Pending or approved may be cancelled with a note; a paid voucher may not — the money has
/// left, and the entry is in the books.
/// </para>
/// <para>
/// <b>Four eyes, with the owner's exception.</b> A manager does not approve a voucher they raised
/// themselves: "the manager's approval" means somebody other than the person asking. An
/// administrator may — the owner is the administrator, and a rule that stopped him signing his
/// own paper would only be worked around.
/// </para>
/// <para>
/// <b>What a refund posts.</b> The early-return relief already credited 411 for the whole unused
/// rent when the goods came back (<see cref="Engine.RentalSettlement.Relieve"/>), so a customer
/// with nothing owing sits with a credit balance. Paying the voucher debits 411 back to zero —
/// Dr 411 (the customer) / Cr 530 or 512 — and raises <see cref="Customer.Balance"/> by the
/// same figure. An expense debits the category's account instead.
/// </para>
/// <para>
/// <b>Answer 27, and why a refund is capped rather than refused.</b> "If the customer owes the
/// company, the refund comes off the debt." The return already did that against the balance of
/// the day. What remains is to stop a voucher paying out a credit that later debt has already
/// eaten: a refund never pays more than the customer's account still holds in their favour, net
/// of refunds already raised and not yet paid. For a voucher over a return note the part above
/// that is recorded as <see cref="PaymentVoucher.DeductedFromDebt"/> — it closes that much of
/// the note's owed figure and posts nothing, because 411 has already netted it. A refund with
/// nothing in the customer's favour at all is refused in words. A refund with no return note
/// behind it is refused rather than capped when it asks for more than the credit: there is no
/// owed figure there for the difference to close.
/// </para>
/// </summary>
public sealed class PaymentVouchers(PosDbContext db, LedgerPoster ledger, IRegionalSettingsProvider regionalSettings)
{
    /// <summary>
    /// The expense categories and the account each one posts to (answer 24: fuel, transport,
    /// repair — and purchase and other kept). ⚠ The accountant confirms the codes; see the
    /// block in <see cref="LedgerAccounts"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ExpenseAccounts { get; } = new Dictionary<string, string>
    {
        ["fuel"] = LedgerAccounts.FuelExpense,
        ["transport"] = LedgerAccounts.TransportExpense,
        ["repair"] = LedgerAccounts.MaintenanceExpense,
        ["purchase"] = LedgerAccounts.SundryPurchases,
        // A day's labour paid on the spot (owner, 2026-10-02): the worker is on no payroll, so the
        // voucher is the whole record. ⚠ The accountant confirms the account, as for the others.
        ["dailyLabour"] = LedgerAccounts.Wages,
        // Workers' bonuses (owner, 2026-11-08): the cash leaves now and is held on staff advances until the payroll run
        // of the workers' month books the bonus as wages and clears it — not an expense here, or it would count twice.
        [WorkersBonus] = LedgerAccounts.StaffAdvances,
        ["other"] = LedgerAccounts.OtherOperatingExpense
    };

    /// <summary>The expense category of a workers' bonus voucher (مكافآت عمال).</summary>
    public const string WorkersBonus = "workersBonus";
    public const int MaxBonusLines = 200;

    /// <summary>The categories whose voucher must name a contract, as the company set them (answer 26).</summary>
    public async Task<IReadOnlySet<string>> ContractCategoriesAsync(CancellationToken ct = default)
    {
        var stored = await db.Stores.AsNoTracking().Select(x => x.VoucherContractCategories).FirstOrDefaultAsync(ct);
        return (stored ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(ExpenseAccounts.ContainsKey).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Every category, its account, and whether the company requires a contract on it.</summary>
    public async Task<IReadOnlyList<ExpenseCategoryDto>> CategoriesAsync(CancellationToken ct = default)
    {
        var required = await ContractCategoriesAsync(ct);
        return ExpenseAccounts.Select(e => new ExpenseCategoryDto(e.Key, e.Value, required.Contains(e.Key))).ToList();
    }

    /// <summary>Sets which categories require a contract; an unknown key is refused in words.</summary>
    public async Task<ReceivableResult<IReadOnlyList<ExpenseCategoryDto>>> SetContractCategoriesAsync(
        IReadOnlyList<string>? keys, CancellationToken ct = default)
    {
        var clean = new List<string>();
        foreach (var key in keys ?? [])
        {
            var k = (key ?? "").Trim();
            if (k.Length == 0) continue;
            var known = ExpenseAccounts.Keys.FirstOrDefault(x => string.Equals(x, k, StringComparison.OrdinalIgnoreCase));
            if (known is null)
                return ReceivableResult<IReadOnlyList<ExpenseCategoryDto>>.Invalid(
                    $"'{k}' is not an expense category ({string.Join(", ", ExpenseAccounts.Keys)}).");
            if (!clean.Contains(known)) clean.Add(known);
        }
        var store = await db.Stores.FirstOrDefaultAsync(ct);
        if (store is null) return ReceivableResult<IReadOnlyList<ExpenseCategoryDto>>.NotFound("Store not found.");
        store.VoucherContractCategories = clean.Count > 0 ? string.Join(",", clean) : null;
        // Not on the till's StoreDto: a plain save, no version bump.
        await db.SaveChangesAsync(ct);
        return ReceivableResult<IReadOnlyList<ExpenseCategoryDto>>.Success(await CategoriesAsync(ct));
    }

    // ---------- raising ----------

    /// <summary>
    /// Raises a voucher. <paramref name="sameBranchOnly"/> is set for a caller bound to a branch
    /// (P0.1b): the contract or return note a refund or an expense names must then be of the
    /// voucher's branch, which CallersBranch has already made theirs; another branch's is answered
    /// exactly as one that does not exist. A refund at one branch for another branch's contract
    /// waits on the owner's rule for it (explicit authorization, branch checks, an audit record).
    /// </summary>
    public async Task<ReceivableResult<(PaymentVoucher Voucher, Warned? Warning)>> CreateAsync(
        CreatePaymentVoucherRequest r, string? by, CancellationToken ct = default, bool sameBranchOnly = false)
    {
        // A payroll voucher is raised by the advance, the wage payment or the statutory payment it
        // records, never on its own: it would be money out with no payroll document behind it.
        if (!Enum.IsDefined(r.Kind) || r.Kind == PaymentVoucherKind.Payroll)
            return Fail.Invalid("A payment voucher is a customer refund or an expense.");
        if (r.Amount < 0) return Fail.Invalid("The amount cannot be negative.");

        if (!await db.Branches.AnyAsync(b => b.Id == r.BranchId && !b.IsDeleted, ct))
            return Fail.NotFound("Branch not found.");

        var key = (r.MethodKey ?? "").Trim().ToLowerInvariant();
        var method = await db.PaymentMethodDefs.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Key == key && m.IsActive && !m.IsDeleted, ct);
        if (method is null) return Fail.NotFound($"No active payment method '{key}'.");

        // The receipt's rule, the other way round: money is not paid out as more credit, as a gift
        // card or as points — each of those moves a balance of its own.
        if (method.Behavior is PaymentBehavior.Credit or PaymentBehavior.GiftCard or PaymentBehavior.Loyalty)
            return Fail.Invalid($"'{key}' cannot pay money out; it moves a balance of its own.");

        // A cheque the company writes is identified by its number and the bank it is drawn on
        // (answer 22). The receipt form checks this for paper coming in; paper going out is the
        // company's own, and a voucher that cannot say which cheque paid it is no record at all.
        if (method.RequiresInstrumentDetails &&
            (string.IsNullOrWhiteSpace(r.InstrumentNumber) || string.IsNullOrWhiteSpace(r.InstrumentBank)))
            return Fail.Invalid($"'{key}' needs the cheque's number and its bank.");

        var voucher = new PaymentVoucher
        {
            BranchId = r.BranchId,
            Kind = r.Kind,
            MethodKey = method.Key,
            InstrumentNumber = method.RequiresInstrumentDetails ? Trim(r.InstrumentNumber, 60) : null,
            InstrumentBank = method.RequiresInstrumentDetails ? Trim(r.InstrumentBank, 120) : null,
            Purpose = Trim(r.Purpose?.Trim(), 300),
            PayeeName = Trim(r.PayeeName?.Trim(), 160),
            Notes = Trim(r.Notes?.Trim(), 500),
            RequestedBy = Trim(by, 100),
            Status = PaymentVoucherStatus.Pending
        };
        if (string.IsNullOrEmpty(voucher.Purpose)) voucher.Purpose = null;
        if (string.IsNullOrEmpty(voucher.PayeeName)) voucher.PayeeName = null;

        Warned? warning = null;
        if (r.Kind == PaymentVoucherKind.Expense)
        {
            var built = BuildExpense(voucher, r);
            if (built is not null) return built;
            if (voucher.ExpenseCategory == WorkersBonus && await BuildBonusAsync(voucher, r, ct) is string bonusProblem)
                return Fail.Invalid(bonusProblem);
            // A workers' bonus is pay, never a rental contract's expense (owner, 2026-11-09): it names none, whatever the
            // company marked; every other category follows answer 26.
            if (voucher.ExpenseCategory == WorkersBonus)
            {
                if (r.ContractId is not null) return Fail.Invalid("A workers' bonus names no rental contract.");
            }
            else if (await ExpenseContractProblemAsync(voucher, r, sameBranchOnly, ct) is string contractProblem)
                return Fail.Invalid(contractProblem);
        }
        else if (r.Kind == PaymentVoucherKind.SupplierPayment)
        {
            if (await BuildSupplierPaymentAsync(voucher, r, ct) is { } built) return built;
        }
        else
        {
            var built = await BuildRefundAsync(voucher, r, sameBranchOnly, ct);
            if (!built.Ok) return Fail.As(built);
            warning = built.Value;
        }

        await using var tx = await db.BeginWriteScopeAsync(ct);

        // Inside the write scope, like every other number: the subscription filter has already
        // taken the version counter's row lock, so two raises cannot count the same total, and
        // the unique index is the backstop if that ever stops being true (answer 20).
        voucher.Number = Numbered("PV", await db.PaymentVouchers.CountAsync(ct) + 1);
        db.PaymentVouchers.Add(voucher);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return ReceivableResult<(PaymentVoucher, Warned?)>.Success((voucher, warning), warning?.Sentence);
    }

    /// <summary>
    /// An expense for a rental contract names it (answer 26). Required where the company says the
    /// category always is; otherwise optional, and a contract named must exist.
    /// </summary>
    private async Task<string?> ExpenseContractProblemAsync(
        PaymentVoucher voucher, CreatePaymentVoucherRequest r, bool sameBranchOnly, CancellationToken ct)
    {
        if (r.ContractId is Guid contractId)
        {
            if (!await db.RentalContracts.AnyAsync(x => x.Id == contractId && (!sameBranchOnly || x.BranchId == voucher.BranchId), ct))
                return "Rental contract not found.";
            voucher.ContractId = contractId;
            return null;
        }
        return (await ContractCategoriesAsync(ct)).Contains(voucher.ExpenseCategory!)
            ? $"A {voucher.ExpenseCategory} expense names the rental contract it is for."
            : null;
    }

    /// <summary>
    /// A payment to a supplier (owner, 2026-10-10): it names the supplier, whose name is the payee,
    /// and it comes off what the company owes them when it is paid. Never refused for paying more
    /// than is owed — an advance to a supplier is ordinary, and their balance simply goes the other way.
    /// </summary>
    private async Task<ReceivableResult<(PaymentVoucher, Warned?)>?> BuildSupplierPaymentAsync(
        PaymentVoucher voucher, CreatePaymentVoucherRequest r, CancellationToken ct)
    {
        if (r.SupplierId is not Guid supplierId) return Fail.Invalid("A supplier payment names the supplier it pays.");
        if (r.CustomerId is not null || r.ReturnNoteId is not null)
            return Fail.Invalid("A supplier payment is not paid to a customer; raise a customer refund instead.");
        if (r.Amount <= 0) return Fail.Invalid("The amount must be above zero.");
        var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == supplierId && !s.IsDeleted, ct);
        if (supplier is null) return Fail.NotFound("Supplier not found.");

        voucher.SupplierId = supplier.Id;
        voucher.PayeeName = Trim(supplier.Name, 160);
        voucher.Amount = r.Amount;
        return null;
    }

    private static ReceivableResult<(PaymentVoucher, Warned?)>? BuildExpense(PaymentVoucher voucher, CreatePaymentVoucherRequest r)
    {
        var raw = (r.ExpenseCategory ?? "").Trim();
        var category = ExpenseAccounts.Keys.FirstOrDefault(k => string.Equals(k, raw, StringComparison.OrdinalIgnoreCase)) ?? "";
        if (!ExpenseAccounts.TryGetValue(category, out var account))
            return Fail.Invalid($"An expense names its category: {string.Join(", ", ExpenseAccounts.Keys)}.");
        if (r.CustomerId is not null || r.ReturnNoteId is not null)
            return Fail.Invalid("An expense is not paid to a customer; raise a customer refund instead.");
        if (category == WorkersBonus)
        {
            // Its workers are the payee and their amounts the total: BuildBonusAsync fills both.
            voucher.ExpenseCategory = category;
            voucher.ExpenseAccountCode = account;
            return null;
        }
        if (r.BonusLines is { Count: > 0 }) return Fail.Invalid("Only a workers' bonus names workers.");
        if (voucher.PayeeName is null)
            return Fail.Invalid("An expense names who is paid.");
        if (r.Amount <= 0) return Fail.Invalid("The amount must be above zero.");

        voucher.ExpenseCategory = category;
        voucher.ExpenseAccountCode = account;
        voucher.Amount = r.Amount;
        return null;
    }

    /// <summary>
    /// A workers' bonus (owner, 2026-11-08): one or more workers of the voucher's branch, each with an amount above zero;
    /// the voucher's amount is their total, its payee their names. The payroll month defaults to the company's current
    /// month. Each worker's line goes on the payslip of that month's run or a later one.
    /// </summary>
    /// <returns>Why it is refused, or null.</returns>
    private async Task<string?> BuildBonusAsync(PaymentVoucher voucher, CreatePaymentVoucherRequest r, CancellationToken ct)
    {
        var lines = r.BonusLines ?? [];
        if (lines.Count == 0) return "A workers' bonus names at least one worker and their amount.";
        if (lines.Count > MaxBonusLines) return $"A workers' bonus names at most {MaxBonusLines} workers.";
        if (lines.GroupBy(l => l.EmployeeId).Any(g => g.Count() > 1)) return "A worker is named twice on the same bonus.";
        if (lines.Any(l => l.Amount <= 0)) return "Each worker's bonus is above zero.";

        var (year, month, monthProblem) = PayrollMonthOf(r.PayrollMonth);
        if (monthProblem is not null) return monthProblem;

        var ids = lines.Select(l => l.EmployeeId).ToList();
        var employees = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id))
            .Select(e => new { e.Id, e.Number, e.FullName, e.BranchId }).ToDictionaryAsync(e => e.Id, ct);
        if (employees.Count != ids.Count) return "A worker named on the bonus was not found.";
        // The bonus is booked to staff advances at this branch and cleared by this branch's payroll: its workers are its own.
        if (employees.Values.FirstOrDefault(e => e.BranchId != voucher.BranchId) is { } elsewhere)
            return $"{elsewhere.FullName} works at another branch; raise their bonus at that branch.";

        foreach (var line in lines)
        {
            var e = employees[line.EmployeeId];
            voucher.BonusLines.Add(new PaymentVoucherBonusLine
            {
                VoucherId = voucher.Id, EmployeeId = e.Id, EmployeeNameSnapshot = e.FullName, EmployeeNumberSnapshot = e.Number,
                Amount = Math.Round(line.Amount, 3, MidpointRounding.AwayFromZero), PayrollYear = year, PayrollMonth = month,
            });
        }
        voucher.Amount = voucher.BonusLines.Sum(l => l.Amount);
        voucher.PayeeName ??= Trim(string.Join(", ", voucher.BonusLines.Select(l => l.EmployeeNameSnapshot)), 160);
        return null;
    }

    /// <summary>"yyyy-MM", or the company's current month on its own clock when blank.</summary>
    private (int Year, int Month, string? Problem) PayrollMonthOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            var zone = Catalog.StoreDocumentHistory.Zone(regionalSettings);
            var today = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, zone);
            return (today.Year, today.Month, null);
        }
        var parts = text.Trim().Split('-');
        if (parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var y)
            && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var m) && y is >= 2000 and <= 2100 && m is >= 1 and <= 12)
            return (y, m, null);
        return (0, 0, "The payroll month is written yyyy-MM.");
    }

    private async Task<ReceivableResult<Warned?>> BuildRefundAsync(
        PaymentVoucher voucher, CreatePaymentVoucherRequest r, bool sameBranchOnly, CancellationToken ct)
    {
        var customerId = r.CustomerId;
        ReturnNote? note = null;
        RefundableReturnNoteDto? owed = null;
        RefundableContractDto? onContract = null;

        if (r.ReturnNoteId is Guid noteId)
        {
            note = await db.ReturnNotes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == noteId, ct);
            if (note is null) return ReceivableResult<Warned?>.NotFound("Return note not found.");
            var contract = await db.RentalContracts.AsNoTracking().FirstAsync(c => c.Id == note.ContractId, ct);
            // Before the customer check, whose refusal names the contract (P0.1b).
            if (sameBranchOnly && contract.BranchId != voucher.BranchId)
                return ReceivableResult<Warned?>.NotFound("Return note not found.");
            if (customerId is Guid named && named != contract.CustomerId)
                return ReceivableResult<Warned?>.Invalid(
                    $"Return note {note.Number} belongs to another customer's contract ({contract.Number}).");
            customerId = contract.CustomerId;
            owed = (await RefundableAsync(null, noteId, ct)).Single();
        }
        else if (r.ContractId is Guid contractId)
        {
            // The unused period of a whole contract (owner, 2026-10-06), for when no return note
            // still owes it — given back off a debt the customer has since paid, say.
            var contract = await db.RentalContracts.AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, ct);
            if (contract is null || contract.Status == RentalContractStatus.Cancelled
                || (sameBranchOnly && contract.BranchId != voucher.BranchId))
                return ReceivableResult<Warned?>.NotFound("Rental contract not found.");
            if (customerId is Guid named && named != contract.CustomerId)
                return ReceivableResult<Warned?>.Invalid($"Contract {contract.Number} is another customer's.");
            customerId = contract.CustomerId;
            onContract = (await RefundableContractsAsync(null, contractId, ct)).Single();
        }

        if (customerId is null) return ReceivableResult<Warned?>.Invalid("A refund names the customer it is paid to.");
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId && !c.IsDeleted, ct);
        if (customer is null) return ReceivableResult<Warned?>.NotFound("Customer not found.");

        var credit = await CreditAsync(customer, ct);
        var requested = r.Amount;
        decimal deducted = 0m;

        if (owed is not null)
        {
            if (owed.StillOwed <= 0)
                return ReceivableResult<Warned?>.Conflict(
                    $"Return note {owed.Number} has nothing left to refund: {owed.AlreadyRaised:0.##} of the " +
                    $"{owed.ReliefPaymentVoucher:0.##} owed there is already on other vouchers.");
            // No figure typed: the voucher is for what is still owed on the note.
            if (requested == 0) requested = owed.StillOwed;
            if (requested > owed.StillOwed)
                return ReceivableResult<Warned?>.Conflict(
                    $"Only {owed.StillOwed:0.##} is still owed on return note {owed.Number}, not {requested:0.##}.");
        }
        else if (onContract is not null)
        {
            if (onContract.StillOwed <= 0)
                return ReceivableResult<Warned?>.Conflict(onContract.UnusedRent <= 0
                    ? $"Contract {onContract.Number} gave back no unused rent: nothing on it is refunded for an unused period."
                    : $"Contract {onContract.Number} has nothing left to refund: {onContract.AlreadyRaised:0.##} of the " +
                      $"{onContract.UnusedRent:0.##} given back is already on other vouchers.");
            // No figure typed: what can be paid now. The credit is checked below, as for any refund.
            if (requested == 0) requested = onContract.Payable > 0 ? onContract.Payable : onContract.StillOwed;
            if (requested > onContract.StillOwed)
                return ReceivableResult<Warned?>.Conflict(
                    $"Only {onContract.StillOwed:0.##} of unused rent is left to refund on contract {onContract.Number}, not {requested:0.##}.");
        }
        else if (requested <= 0)
        {
            return ReceivableResult<Warned?>.Invalid("The amount must be above zero.");
        }

        // Answer 27: what the customer owes comes first, and nothing is paid to someone whose
        // account holds nothing in their favour.
        if (credit <= 0)
            return ReceivableResult<Warned?>.Conflict(customer.Balance > 0
                ? $"{customer.FullName} owes {customer.Balance:0.##}; a refund is taken off that debt, so nothing is paid out."
                : $"{customer.FullName}'s account holds nothing in their favour once the refunds already raised are paid, so nothing is paid out.");

        if (requested > credit)
        {
            if (owed is null)
                return ReceivableResult<Warned?>.Conflict(
                    $"{customer.FullName}'s account holds only {credit:0.##} in their favour, not {requested:0.##}.");
            deducted = requested - credit;
            requested = credit;
        }

        voucher.CustomerId = customer.Id;
        voucher.PayeeName ??= Trim(customer.FullName, 160);
        voucher.Amount = requested;
        voucher.DeductedFromDebt = deducted;
        if (note is not null && owed is not null)
        {
            voucher.ReturnNoteId = note.Id;
            voucher.ContractId = note.ContractId;
            voucher.UnusedDays = owed.UnusedDays;
            // Answer 26: the paper names the contract, the note and the days, and the clerk may
            // still write something else.
            voucher.Purpose ??= Trim(RefundPurpose(owed), 300);
        }
        else if (onContract is not null)
        {
            voucher.ContractId = onContract.ContractId;
            voucher.Purpose ??= Trim($"Remboursement — contrat {onContract.Number}, période non utilisée", 300);
        }

        return ReceivableResult<Warned?>.Success(deducted > 0
            ? new Warned(
                $"{deducted:0.##} of the refund was taken off what {customer.FullName} owes; {requested:0.##} is paid out.",
                PaymentVoucherWarnings.TakenOffDebt,
                new Dictionary<string, string> { ["deducted"] = Figure(deducted), ["paid"] = Figure(requested) })
            : null);
    }

    /// <summary>"Remboursement — contrat CTR-000012, retour RET-000004, 18 jours non utilisés": French, the document language (answer 4), and editable.</summary>
    private static string RefundPurpose(RefundableReturnNoteDto owed)
    {
        var text = $"Remboursement — contrat {owed.ContractNumber}, retour {owed.Number}";
        return owed.UnusedDays is int days && days > 0 ? $"{text}, {days} jours non utilisés" : text;
    }

    // ---------- the decisions ----------

    public async Task<ReceivableResult<(PaymentVoucher Voucher, Warned? Warning)>> ApproveAsync(
        Guid id, string? note, VoucherActor actor, CancellationToken ct = default)
    {
        var voucher = await db.PaymentVouchers.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (voucher is null) return Fail.NotFound("Payment voucher not found.");
        if (voucher.Status != PaymentVoucherStatus.Pending)
            return Fail.Conflict($"{voucher.Number} is {Word(voucher.Status)}; only a pending voucher is approved.");

        if (!actor.MayApproveOwn && !string.IsNullOrEmpty(voucher.RequestedBy) &&
            string.Equals(voucher.RequestedBy, actor.Username, StringComparison.OrdinalIgnoreCase))
            return Fail.Conflict(
                $"{voucher.Number} was raised by you; another manager or an administrator approves it.");

        var name = await NameOfAsync(actor, ct);

        await using var tx = await db.BeginWriteScopeAsync(ct);
        voucher.Status = PaymentVoucherStatus.Approved;
        voucher.ApprovedByUserId = actor.UserId;
        voucher.ApprovedByName = Trim(name, 120);
        voucher.ApprovedAtUtc = DateTime.UtcNow;
        voucher.StatusNote = Trim(note?.Trim(), 300) is { Length: > 0 } n ? n : voucher.StatusNote;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return ReceivableResult<(PaymentVoucher, Warned?)>.Success((voucher, null));
    }

    /// <summary>
    /// The money leaves: the cash movement, the ledger entry and — on a refund — the customer's
    /// balance, all in one unit of work.
    ///
    /// <para>
    /// ⚠ A short box warns and never refuses — the remittance and payroll precedent. The count is
    /// often behind a refill nobody has recorded yet, and a manager standing at the counter with
    /// the cash in hand is not helped by a screen that will not let the paper say so.
    /// </para>
    /// </summary>
    public async Task<ReceivableResult<(PaymentVoucher Voucher, Warned? Warning)>> PayAsync(
        Guid id, string? note, VoucherActor actor, CancellationToken ct = default)
    {
        var by = actor.Username;

        // Everything below is read under the version lock: the voucher's status, so two clicks
        // cannot both pay it, and the customer's credit, so the check is made against the debt a
        // till may have added a moment ago rather than the figure from before it.
        await using var tx = await db.BeginWriteScopeAsync(ct);
        await db.LockVersionsAsync(ct);

        var voucher = await db.PaymentVouchers.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (voucher is null) return Fail.NotFound("Payment voucher not found.");
        switch (voucher.Status)
        {
            case PaymentVoucherStatus.Pending:
                return Fail.Conflict($"{voucher.Number} has not been approved; a manager approves it before it is paid.");
            case PaymentVoucherStatus.Paid:
                return Fail.Conflict(string.Create(CultureInfo.InvariantCulture, $"{voucher.Number} was already paid on {voucher.PaidAtUtc:yyyy-MM-dd}."));
            case PaymentVoucherStatus.Cancelled:
                return Fail.Conflict($"{voucher.Number} was cancelled; raise a new voucher.");
        }

        // The tender as it is now, active or not: it was valid when the voucher was raised and
        // approved, and retiring it since does not un-owe the money.
        var method = await db.PaymentMethodDefs.AsNoTracking().FirstOrDefaultAsync(m => m.Key == voucher.MethodKey, ct);
        if (method is null) return Fail.Conflict($"The payment method '{voucher.MethodKey}' no longer exists.");

        Customer? customer = null;
        Supplier? supplier = null;
        string debit;
        if (voucher.Kind == PaymentVoucherKind.SupplierPayment)
        {
            // Under the version lock taken above: a receiving note may be adding to this balance.
            supplier = await db.LockedAsync<Supplier>(voucher.SupplierId ?? Guid.Empty, ct);
            if (supplier is null) return Fail.NotFound("Supplier not found.");
            debit = LedgerAccounts.Suppliers;
        }
        else if (voucher.Kind == PaymentVoucherKind.CustomerRefund)
        {
            customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == voucher.CustomerId, ct);
            if (customer is null) return Fail.NotFound("Customer not found.");
            // Answer 27 again, at the moment the money moves: debt raised since the approval has
            // already eaten into the credit, and paying through it would lend the customer cash.
            // M7b: less what advances hold on 419 (none under the default setting), which a refund cannot debit.
            var favour = -customer.Balance - (await CustomerAdvances.OpenAsync(db, customer.Id, ct)).Values.Sum();
            if (favour < voucher.Amount)
                return Fail.Conflict(
                    $"{customer.FullName}'s account now holds only {Math.Max(0m, favour):0.##} in their favour, " +
                    $"not {voucher.Amount:0.##}; cancel {voucher.Number} and raise it again.");
            debit = LedgerAccounts.Customers;
        }
        else
        {
            if (voucher.ExpenseCategory is null || !ExpenseAccounts.TryGetValue(voucher.ExpenseCategory, out var account))
                return Fail.Conflict($"{voucher.Number} names no expense category this system posts.");
            voucher.ExpenseAccountCode = account;
            debit = account;
        }

        var paidAt = DateTime.UtcNow;

        voucher.Status = PaymentVoucherStatus.Paid;
        voucher.PaidAtUtc = paidAt;
        voucher.PaidByName = Trim(await NameOfAsync(actor, ct), 120);
        if (Trim(note?.Trim(), 300) is { Length: > 0 } n) voucher.StatusNote = n;

        var cash = method.Behavior == PaymentBehavior.Cash;
        if (PaymentVoucherPosting.Paid(voucher, debit, cash, by) is PostingDraft draft)
            await ledger.PostAsync(draft, ct);

        if (customer is not null)
        {
            customer.Balance += voucher.Amount;
            await db.TouchAsync(customer, ct);
        }
        if (supplier is not null)
        {
            // At the ledger's scale, like the receiving note that raised what is owed.
            supplier.Balance -= Math.Round(voucher.Amount, 3, MidpointRounding.AwayFromZero);
            supplier.UpdatedAtUtc = paidAt;
        }

        Warned? warning = null;
        // Only physical cash leaves a drawer; a cheque or a transfer is money at the bank.
        if (cash) warning = await TakeCashAsync(voucher, by, paidAt, ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return ReceivableResult<(PaymentVoucher, Warned?)>.Success((voucher, warning), warning?.Sentence);
    }

    public async Task<ReceivableResult<(PaymentVoucher Voucher, Warned? Warning)>> CancelAsync(
        Guid id, string? note, CancellationToken ct = default)
    {
        var voucher = await db.PaymentVouchers.FirstOrDefaultAsync(v => v.Id == id, ct);
        if (voucher is null) return Fail.NotFound("Payment voucher not found.");
        if (voucher.Status == PaymentVoucherStatus.Paid)
            return Fail.Conflict(
                string.Create(CultureInfo.InvariantCulture, $"{voucher.Number} was paid on {voucher.PaidAtUtc:yyyy-MM-dd}. A paid voucher is not cancelled: ") +
                "the money has left the box and the entry is in the books.");
        if (voucher.Status == PaymentVoucherStatus.Cancelled)
            return Fail.Conflict($"{voucher.Number} is already cancelled.");
        var reason = Trim(note?.Trim(), 300);
        if (string.IsNullOrEmpty(reason)) return Fail.Invalid("Say why the voucher is cancelled.");

        await using var tx = await db.BeginWriteScopeAsync(ct);
        voucher.Status = PaymentVoucherStatus.Cancelled;
        voucher.CancelledAtUtc = DateTime.UtcNow;
        voucher.StatusNote = reason;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return ReceivableResult<(PaymentVoucher, Warned?)>.Success((voucher, null));
    }

    // ---------- reading ----------

    public Task<PaymentVoucher?> ReadAsync(Guid id, CancellationToken ct = default) =>
        db.PaymentVouchers.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);

    /// <summary>
    /// Return notes whose refund was left owed as a voucher, and how much of it is still to be
    /// raised. Filtered to one customer or to one note; with neither, every note that still has
    /// something owed. A note asked for by id is returned even when nothing is left on it, so the
    /// form can say so rather than find nothing.
    /// </summary>
    public async Task<List<RefundableReturnNoteDto>> RefundableAsync(
        Guid? customerId, Guid? returnNoteId, CancellationToken ct = default, Guid? branchId = null)
    {
        var query = db.ReturnNotes.AsNoTracking()
            .Join(db.RentalContracts.AsNoTracking(), n => n.ContractId, c => c.Id, (n, c) => new { Note = n, Contract = c });
        // The contract's branch, as refundable-contracts reads it: another branch's note is not
        // listed, and asked for by id it comes back as one that does not exist (P0.1b).
        if (branchId is Guid branch) query = query.Where(x => x.Contract.BranchId == branch);
        if (returnNoteId is Guid id) query = query.Where(x => x.Note.Id == id);
        if (customerId is Guid cid) query = query.Where(x => x.Contract.CustomerId == cid);

        // In memory: SQLite stores decimal as TEXT, so "ReliefPaymentVoucher > 0" in SQL would
        // compare strings. The notes of one customer are a short list.
        var rows = (await query.ToListAsync(ct))
            .Where(x => returnNoteId is not null || x.Note.ReliefPaymentVoucher > 0)
            .ToList();
        if (rows.Count == 0) return [];

        var noteIds = rows.Select(x => x.Note.Id).ToList();
        var raised = (await db.PaymentVouchers.AsNoTracking()
                .Where(v => v.ReturnNoteId != null && noteIds.Contains(v.ReturnNoteId!.Value)
                            && v.Status != PaymentVoucherStatus.Cancelled)
                .Select(v => new { v.ReturnNoteId, v.Amount, v.DeductedFromDebt })
                .ToListAsync(ct))
            .GroupBy(v => v.ReturnNoteId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(v => v.Amount + v.DeductedFromDebt));

        var customerIds = rows.Select(x => x.Contract.CustomerId).Distinct().ToList();
        var customers = await db.Customers.AsNoTracking()
            .Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var credits = new Dictionary<Guid, decimal>();
        foreach (var customer in customers.Values) credits[customer.Id] = await CreditAsync(customer, ct);

        var days = await UnusedDaysAsync(rows.Select(x => x.Note).ToList(), ct);

        return rows
            .OrderBy(x => x.Note.ReturnedAtUtc)
            .Select(x =>
            {
                var already = raised.GetValueOrDefault(x.Note.Id);
                var still = Math.Max(0m, x.Note.ReliefPaymentVoucher - already);
                var credit = credits.GetValueOrDefault(x.Contract.CustomerId);
                return new RefundableReturnNoteDto(
                    x.Note.Id, x.Note.Number, x.Contract.Id, x.Contract.Number,
                    x.Contract.CustomerId, customers.GetValueOrDefault(x.Contract.CustomerId)?.FullName ?? "",
                    x.Note.BranchId, x.Note.ReturnedAtUtc,
                    x.Note.UnusedRent, x.Note.ReliefDebtReduction, x.Note.ReliefPaymentVoucher,
                    already, still, credit, Math.Min(still, credit),
                    days.GetValueOrDefault(x.Note.Id));
            })
            .Where(x => returnNoteId is not null || x.StillOwed > 0)
            .ToList();
    }

    /// <summary>
    /// The contracts whose customer may still be refunded for an unused period (owner,
    /// 2026-10-06): what their return notes gave back, less every refund voucher not cancelled
    /// that names the contract or one of its notes. Filtered to one customer or one contract; a
    /// contract asked for by id comes back even when nothing is left, so the form can say so.
    /// Cancelled contracts are never offered.
    /// </summary>
    public async Task<List<RefundableContractDto>> RefundableContractsAsync(
        Guid? customerId, Guid? contractId, CancellationToken ct = default, Guid? branchId = null)
    {
        var contracts = db.RentalContracts.AsNoTracking().Where(c => c.Status != RentalContractStatus.Cancelled);
        if (branchId is Guid branch) contracts = contracts.Where(c => c.BranchId == branch);
        if (contractId is Guid id) contracts = contracts.Where(c => c.Id == id);
        if (customerId is Guid cid) contracts = contracts.Where(c => c.CustomerId == cid);
        var rows = await contracts.Select(c => new { c.Id, c.Number, c.CustomerId }).ToListAsync(ct);
        if (rows.Count == 0) return [];

        var ids = rows.Select(x => x.Id).ToList();
        // In memory: SQLite stores decimal as TEXT.
        var notes = await db.ReturnNotes.AsNoTracking().Where(n => ids.Contains(n.ContractId))
            .Select(n => new { n.Id, n.ContractId, n.UnusedRent }).ToListAsync(ct);
        var unused = notes.GroupBy(n => n.ContractId).ToDictionary(g => g.Key, g => g.Sum(n => n.UnusedRent));
        var contractOfNote = notes.ToDictionary(n => n.Id, n => n.ContractId);
        var noteIds = contractOfNote.Keys.ToList();

        var raised = (await db.PaymentVouchers.AsNoTracking()
                .Where(v => v.Kind == PaymentVoucherKind.CustomerRefund && v.Status != PaymentVoucherStatus.Cancelled
                            && ((v.ContractId != null && ids.Contains(v.ContractId.Value))
                                || (v.ReturnNoteId != null && noteIds.Contains(v.ReturnNoteId.Value))))
                .Select(v => new { v.ContractId, v.ReturnNoteId, v.Amount, v.DeductedFromDebt })
                .ToListAsync(ct))
            .GroupBy(v => v.ContractId ?? contractOfNote[v.ReturnNoteId!.Value])
            .ToDictionary(g => g.Key, g => g.Sum(v => v.Amount + v.DeductedFromDebt));

        var customerIds = rows.Select(x => x.CustomerId).Distinct().ToList();
        var customers = await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var credits = new Dictionary<Guid, decimal>();
        foreach (var customer in customers.Values) credits[customer.Id] = await CreditAsync(customer, ct);

        var result = new List<RefundableContractDto>();
        foreach (var row in rows.OrderBy(x => x.Number, StringComparer.Ordinal))
        {
            var given = unused.GetValueOrDefault(row.Id);
            var already = raised.GetValueOrDefault(row.Id);
            var still = Math.Max(0m, given - already);
            if (contractId is null && still <= 0) continue;
            var credit = credits.GetValueOrDefault(row.CustomerId);
            result.Add(new RefundableContractDto(
                row.Id, row.Number, row.CustomerId, customers.GetValueOrDefault(row.CustomerId)?.FullName ?? "",
                given, already, still, credit, Math.Min(still, credit)));
        }
        return result;
    }

    /// <summary>
    /// What the customer's account holds in their favour once every refund already raised and not
    /// yet paid is paid: the credit a new refund may still pay out.
    /// </summary>
    private Task<decimal> CreditAsync(Customer customer, CancellationToken ct) => RefundableCreditAsync(db, customer, ct);

    /// <summary>
    /// The customer's credit on 411 a refund may still pay out — also the cap on what of a receipt's unallocated part may be
    /// kept as other income (SAL-13): the same money.
    /// </summary>
    public static async Task<decimal> RefundableCreditAsync(PosDbContext db, Customer customer, CancellationToken ct = default)
    {
        var unpaid = (await db.PaymentVouchers.AsNoTracking()
                .Where(v => v.CustomerId == customer.Id && v.Kind == PaymentVoucherKind.CustomerRefund
                            && (v.Status == PaymentVoucherStatus.Pending || v.Status == PaymentVoucherStatus.Approved))
                .Select(v => v.Amount)
                .ToListAsync(ct))
            .Sum();
        // M7b: an advance still held on 419 is the customer's money but not on 411, which a refund debits: it is released
        // to the account first (its VAT given back with it). Nothing is held there under the default setting.
        var onAdvances = (await CustomerAdvances.OpenAsync(db, customer.Id, ct)).Values.Sum();
        return Math.Max(0m, -customer.Balance - unpaid - onAdvances);
    }

    /// <summary>
    /// The days of the agreed period the customer paid for and did not use, per return note
    /// (answer 26).
    ///
    /// <para>
    /// The note does not store a day count — it stores the unused <i>money</i>, frozen at the
    /// return. The days are derived from what it does store: each rent charge the note raised
    /// inside the agreed period carries the days it charged, and the line's period minus those
    /// days is what went unused. That is exactly the figure the settlement engine refunded
    /// against (<c>RentalPeriod.UnusedDays</c>). Several lines on one note normally share one
    /// period; the largest is printed. A note whose goods came back on the day they left raised
    /// no rent at all (a zero amount is not stored), so the whole period is unused. A note
    /// that refunded nothing has no unused days.
    /// </para>
    /// </summary>
    private async Task<Dictionary<Guid, int?>> UnusedDaysAsync(IReadOnlyList<ReturnNote> notes, CancellationToken ct)
    {
        var ids = notes.Where(n => n.UnusedRent > 0).Select(n => n.Id).ToList();
        var result = notes.ToDictionary(n => n.Id, _ => (int?)null);
        if (ids.Count == 0) return result;

        var charges = await db.RentalCharges.AsNoTracking()
            .Where(c => ids.Contains(c.ReturnNoteId) && c.Kind == RentalChargeKind.Rent && c.ExtensionId == null)
            .Select(c => new { c.ReturnNoteId, c.ContractLineId, c.Days })
            .ToListAsync(ct);
        var returnedLines = await db.ReturnNoteLines.AsNoTracking()
            .Where(l => ids.Contains(l.ReturnNoteId))
            .Select(l => new { l.ReturnNoteId, l.ContractLineId })
            .ToListAsync(ct);
        var allocations = await db.Set<ReturnNoteLineAllocation>().AsNoTracking()
            .Join(db.ReturnNoteLines.Where(l => ids.Contains(l.ReturnNoteId)),
                a => a.ReturnNoteLineId, l => l.Id,
                (a, l) => new { l.ReturnNoteId, a.ContractLineId, a.ChargeableDays, a.UnusedRentNet })
            .ToListAsync(ct);

        var lineIds = charges.Select(c => c.ContractLineId).Concat(returnedLines.Select(l => l.ContractLineId)).Distinct().ToList();
        var contractIds = notes.Select(n => n.ContractId).Distinct().ToList();
        var lines = await db.RentalContractLines.AsNoTracking()
            .Where(l => contractIds.Contains(l.ContractId))
            .ToListAsync(ct);
        var engineLines = lines.Select(Rentals.RentalDocuments.EngineLine).ToList();
        int Duration(Guid lineId) =>
            engineLines.FirstOrDefault(l => l.Id == lineId) is { } line
                ? Engine.RentalSettlement.DurationOf(line, engineLines)
                : 0;

        foreach (var id in ids)
        {
            var snapshots = allocations.Where(a => a.ReturnNoteId == id).ToList();
            if (snapshots.Count > 0)
            {
                var unused = snapshots.Where(a => a.UnusedRentNet > 0)
                    .Select(a => Engine.RentalPeriod.UnusedDays(a.ChargeableDays, Duration(a.ContractLineId)))
                    .DefaultIfEmpty(0).Max();
                result[id] = unused > 0 ? unused : null;
                continue;
            }
            var rent = charges.Where(c => c.ReturnNoteId == id).ToList();
            int? days = rent.Count > 0
                ? rent.Max(c => Engine.RentalPeriod.UnusedDays(c.Days, Duration(c.ContractLineId)))
                : returnedLines.Where(l => l.ReturnNoteId == id).Select(l => Duration(l.ContractLineId)).DefaultIfEmpty(0).Max();
            result[id] = days is > 0 ? days : null;
        }

        return result;
    }

    // ---------- plumbing ----------

    /// <summary>The name the paper prints for whoever acted: their full name, else their login.</summary>
    private async Task<string?> NameOfAsync(VoucherActor actor, CancellationToken ct)
    {
        if (actor.UserId is not Guid userId) return actor.Username;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        return user is not null && !string.IsNullOrWhiteSpace(user.FullName) ? user.FullName : actor.Username;
    }

    /// <summary>Money out of the branch's box, and the warning when the count says there is less in it than that.</summary>
    private async Task<Warned?> TakeCashAsync(PaymentVoucher voucher, string? by, DateTime at, CancellationToken ct)
    {
        var box = await db.CashBoxes.FirstOrDefaultAsync(x => x.BranchId == voucher.BranchId && x.IsActive, ct);
        if (box is null)
            return new Warned(
                "This branch has no open cash box, so no drawer movement was recorded; the books show the cash leaving.",
                PaymentVoucherWarnings.NoCashBox, new Dictionary<string, string>());

        var balance = await CashBoxBalance.ForWriteAsync(db, box.Id, ct);

        db.CashMovements.Add(new CashMovement
        {
            CashBoxId = box.Id,
            Direction = CashDirection.Out,
            Amount = voucher.Amount,
            BalanceAfter = balance - voucher.Amount,
            // The kind the wage payment already writes (docs/07 §5.10): money out against a voucher.
            Kind = CashMovementKind.Disbursement,
            SourceType = LedgerSources.PaymentVoucher,
            SourceId = voucher.Id,
            UserName = Trim(by, 200),
            Reference = voucher.Number,
            Note = Trim(voucher.Purpose, 400),
            OccurredAtUtc = at
        });

        return voucher.Amount > balance
            ? new Warned(
                $"Paying {voucher.Amount:0.##} out of '{box.NameFr}', which the count puts at {balance:0.##}.",
                PaymentVoucherWarnings.BoxShort,
                new Dictionary<string, string>
                {
                    ["amount"] = Figure(voucher.Amount), ["balance"] = Figure(balance), ["box"] = box.NameFr
                })
            : null;
    }

    private string Figure(decimal value) => regionalSettings.Get().RoundMoney(value).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Word(PaymentVoucherStatus status) => status switch
    {
        PaymentVoucherStatus.Approved => "already approved",
        PaymentVoucherStatus.Paid => "already paid",
        PaymentVoucherStatus.Cancelled => "cancelled",
        _ => "pending"
    };

    private static string Numbered(string prefix, int next) => $"{prefix}-{next:D6}";

    private static string? Trim(string? value, int max) =>
        value is { Length: > 0 } && value.Length > max ? value[..max] : value;

    /// <summary>The failures, typed once so every early return reads as one word.</summary>
    private static class Fail
    {
        public static ReceivableResult<(PaymentVoucher, Warned?)> NotFound(string m) => ReceivableResult<(PaymentVoucher, Warned?)>.NotFound(m);
        public static ReceivableResult<(PaymentVoucher, Warned?)> Invalid(string m) => ReceivableResult<(PaymentVoucher, Warned?)>.Invalid(m);
        public static ReceivableResult<(PaymentVoucher, Warned?)> Conflict(string m) => ReceivableResult<(PaymentVoucher, Warned?)>.Conflict(m);
        public static ReceivableResult<(PaymentVoucher, Warned?)> As<T>(ReceivableResult<T> r) => new(default, r.Failure, r.Message);
    }
}

/// <summary>A warning with its code and figures beside the English sentence.</summary>
public sealed record Warned(string Sentence, string Code, IReadOnlyDictionary<string, string> Args);

/// <summary>
/// What paying a voucher means in the ledger. Pure and static like the other posting rules, so
/// the balance of it can be checked without a server.
/// </summary>
public static class PaymentVoucherPosting
{
    /// <summary>
    /// Dr the customer's account (a refund) or the expense (anything else) / Cr the cash box or
    /// the bank. A refund's debit carries the customer, which is what puts it on their statement.
    /// Null for a voucher of nothing, which an entry with no lines would only clutter reports with.
    /// </summary>
    public static PostingDraft? Paid(PaymentVoucher voucher, string debitAccount, bool cash, string? by)
    {
        if (voucher.Amount <= 0) return null;
        var refund = voucher.Kind == PaymentVoucherKind.CustomerRefund;
        var supplier = voucher.Kind == PaymentVoucherKind.SupplierPayment;

        return new PostingDraft
        {
            EntryDate = voucher.PaidAtUtc ?? DateTime.UtcNow,
            BranchId = voucher.BranchId,
            SourceType = LedgerSources.PaymentVoucher,
            SourceId = voucher.Id,
            Description = refund
                ? $"Payment voucher {voucher.Number} — refund"
                : supplier
                    ? $"Payment voucher {voucher.Number} — supplier {voucher.PayeeName}"
                    : $"Payment voucher {voucher.Number} — {voucher.ExpenseCategory}",
            CreatedBy = by
        }
            .Add(PostingLine.Dr(debitAccount, voucher.Amount, memo: voucher.Number,
                customerId: refund ? voucher.CustomerId : null,
                supplierId: supplier ? voucher.SupplierId : null))
            .Add(PostingLine.Cr(cash ? LedgerAccounts.Cash : LedgerAccounts.Bank, voucher.Amount, memo: voucher.Number));
    }
}
