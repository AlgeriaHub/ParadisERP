using System.Globalization;
using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Customers;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.FixedAssets;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Persistence;
using AlgeriaPos.Infrastructure.Valuation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Engine = AlgeriaPos.Application.Rentals;

namespace AlgeriaPos.Infrastructure.Rentals;

/// <summary>Why a rental document could not be raised, in the shape an HTTP layer needs.</summary>
public enum RentalFailure { NotFound, Invalid, Conflict, Forbidden }

/// <summary>A document, or the reason there isn't one.</summary>
public sealed record RentalResult<T>(T? Value, RentalFailure? Failure = null, string? Message = null, string? Code = null)
{
    public bool Ok => Failure is null;

    public static RentalResult<T> Success(T value) => new(value);
    public static RentalResult<T> NotFound(string message) => new(default, RentalFailure.NotFound, message);
    public static RentalResult<T> Invalid(string message) => new(default, RentalFailure.Invalid, message);
    public static RentalResult<T> Conflict(string message) => new(default, RentalFailure.Conflict, message);
    public static RentalResult<T> Forbidden(string message, string? code = null) => new(default, RentalFailure.Forbidden, message, code);
}

/// <summary>One quantity that had to move branch because the goods came back somewhere else.</summary>
public sealed record ImplicitTransfer(Guid ProductId, Guid FromBranchId, Guid ToBranchId, decimal Qty, StockState State);

/// <summary>
/// What raising an issue note produced: the note, and the period it invoiced. The figure is
/// this note's own, not the contract's running total — a second delivery would otherwise report
/// the first one's money again.
/// </summary>
public sealed record IssueOutcome(IssueNote Note, decimal PeriodRent);

/// <summary>
/// What one contract has put out, settled and charged, read off the stored documents rather than
/// by running the engine (docs/07 §3.1). <see cref="ChargeTotal"/> is <b>what was charged</b>:
/// the sum of the stored <c>RentalCharges</c>, which is the settlement's total by construction
/// because <c>StoreCharges</c> writes every engine charge including unposted in-period rent.
/// </summary>
public sealed record ContractTotals(
    decimal IssuedQty, decimal SettledQty, decimal StillOutQty, decimal ChargeTotal, decimal OverdueTotal)
{
    public static readonly ContractTotals Zero = new(0m, 0m, 0m, 0m, 0m);
}

/// <summary>One issue batch still out: which contract line, issued from which branch, on which day its
/// period started, the day the agreed period ends, and how much of it is still with the customer.</summary>
public sealed record OutstandingBatch(
    Guid ContractId, RentalContractLine Line, Guid IssueNoteId, Guid IssueBranchId,
    DateOnly IssuedOn, DateOnly EndsOn, decimal Qty);

/// <summary>What raising a return note produced, beyond the note itself.</summary>
public sealed record ReturnOutcome(
    ReturnNote Note,
    IReadOnlyList<RentalCharge> Charges,
    IReadOnlyList<ImplicitTransfer> Transfers,
    bool ContractClosed, ReturnPreviewDto? Preview = null);

/// <summary>
/// Raising the documents of a rental, and everything each one implies: stock moving between
/// states, quantities moving between branches, and money reaching the ledger.
///
/// <para>
/// <b>None of the arithmetic lives here.</b> <c>AlgeriaPos.Application.Rentals</c> owns the
/// day counting, the FIFO match, the pricing and the rules about what may be raised; this
/// class feeds it real documents and stores what it hands back. Where a decision looks
/// arithmetic — which batch a returned unit came off, whether a contract may close, whether an
/// extension is retroactive — it is asked of the engine rather than recomputed, so there is
/// exactly one answer in the system.
/// </para>
/// <para>
/// A service rather than a set of endpoint lambdas because these are the rules worth testing:
/// that a contract locks, that it closes on the right condition, that issuing moves stock the
/// way the stock module already moves it, and that a return taken at the wrong branch does not
/// quietly leave two branches' balances wrong.
/// </para>
/// </summary>
public sealed partial class RentalDocuments(
    PosDbContext db, LedgerPoster ledger, IRegionalSettingsProvider regionalSettings, ILogger<RentalDocuments>? logger = null)
{
    /// <summary>A return note named at a branch other than the contract's; {0} is the contract's branch.</summary>
    public const string ReturnAtContractBranchMessage =
        "A return note is taken at the contract's branch ({0}). Move the goods on afterwards with a stock transfer.";

    // ---------- settings ----------

    /// <summary>
    /// The company's rental settings. Both live on the store row beside the loyalty programme,
    /// because they are company business rules an admin owns — see <see cref="Store"/>.
    /// </summary>
    public async Task<Engine.RentalSettings> SettingsAsync(CancellationToken ct = default)
    {
        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => !s.IsDeleted, ct);
        return store is null
            ? new Engine.RentalSettings()
            : new Engine.RentalSettings(store.RentalOverdueMultiplier, store.RentalDepositEnabled, store.RentalDayCutoffHour,
                store.RentalMinimumDays);
    }

    /// <summary>
    /// The rate rental documents are taxed at. Kept off the engine's settings record because
    /// the engine does no tax arithmetic; a field it ignored would only invite miswiring.
    /// </summary>
    public async Task<decimal> TaxRateAsync(CancellationToken ct = default) =>
        (await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => !s.IsDeleted, ct))?.RentalTaxRate ?? 0m;

    public async Task<RentalResult<Store>> UpdateSettingsAsync(UpdateRentalSettingsRequest r, CancellationToken ct = default)
    {
        var settings = new Engine.RentalSettings(r.OverdueMultiplier, r.DepositEnabled, r.DayCutoffHour, r.MinimumDays);
        try { Engine.RentalRules.Validate(settings); }
        catch (ArgumentException ex) { return RentalResult<Store>.Invalid(ex.Message); }

        var store = await db.Stores.FirstOrDefaultAsync(s => !s.IsDeleted, ct);
        if (store is null) return RentalResult<Store>.NotFound("No store profile has been created.");

        // A negative rate is a typo, and above 100 is not a tax rate. Zero is legitimate and
        // is the default: it means hire is not taxed here.
        if (r.TaxRate is < 0 or > 100)
            return RentalResult<Store>.Invalid("The rental tax rate must be between 0 and 100.");

        store.RentalOverdueMultiplier = r.OverdueMultiplier;
        store.RentalDepositEnabled = r.DepositEnabled;
        store.RentalTaxRate = r.TaxRate;
        // Null keeps the rule the engine has always had; an hour splits the day for delivery and
        // return (answers 5 and 6). A batch's start day is frozen on its issue note, so a new
        // hour reaches new deliveries only: notes that predate the frozen column are frozen here
        // with the hour they were raised under before it changes.
        if (store.RentalDayCutoffHour != r.DayCutoffHour)
        {
            var zone = Zone();
            var unfrozen = await db.IssueNotes.Where(n => n.PeriodStartsOn == null).ToListAsync(ct);
            foreach (var n in unfrozen) n.PeriodStartsOn = StartsOn(n, zone, store.RentalDayCutoffHour);
        }
        store.RentalDayCutoffHour = r.DayCutoffHour;
        // Frozen on each contract as it is raised, so this reaches new contracts only.
        store.RentalMinimumDays = r.MinimumDays;
        // A store row is replicated, so it is versioned like every other catalog write — the
        // two new columns simply do not travel in the payload.
        await db.SaveWithVersionsAsync(ct, store);
        return RentalResult<Store>.Success(store);
    }

    /// <param name="damageUnitPrice">
    /// The item's fixed replacement value (answer 13), which a contract line starts from. Null
    /// clears it, like the multiplier: only the rental settings screen calls this route, and it
    /// always sends the value it shows.
    /// </param>
    public async Task<RentalResult<Product>> SetProductMultiplierAsync(
        Guid productId, decimal? multiplier, decimal? rentalUnitPrice = null, decimal? damageUnitPrice = null,
        CancellationToken ct = default)
    {
        if (multiplier is decimal m && (m < 1 || m > 10))
            return RentalResult<Product>.Invalid("An item's overdue multiplier must be between 1 and 10, or absent.");
        if (rentalUnitPrice is decimal price && price < 0)
            return RentalResult<Product>.Invalid("An item's rental price cannot be negative.");
        if (Engine.RentalRules.DamageValueProblem(damageUnitPrice) is not null)
            return RentalResult<Product>.Invalid("An item's replacement value cannot be negative; leave it blank to have none.");

        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId && !p.IsDeleted, ct);
        if (product is null) return RentalResult<Product>.NotFound("Product not found.");

        product.RentalOverdueMultiplier = multiplier;
        if (rentalUnitPrice is decimal configuredPrice) product.RentalUnitPrice = configuredPrice;
        product.RentalDamageUnitPrice = damageUnitPrice;
        await db.SaveWithVersionsAsync(ct, product);
        return RentalResult<Product>.Success(product);
    }

    /// <summary>
    /// Every item carrying its own overdue multiplier, by SKU (docs/07 §3.2). Until this existed
    /// the override could be written and never read: the PUT's response was the only place the
    /// figure appeared, so the settings screen could not list what was set and the contract form
    /// previewed overdue with the company figure for an item that had its own.
    /// </summary>
    public async Task<IReadOnlyList<ProductOverdueMultiplierDto>> ProductMultipliersAsync(CancellationToken ct = default) =>
        await db.Products.AsNoTracking()
            .Where(p => !p.IsDeleted && (p.RentalOverdueMultiplier != null || p.RentalUnitPrice > 0 || p.RentalDamageUnitPrice != null))
            .OrderBy(p => p.Sku)
            .Select(p => new ProductOverdueMultiplierDto(
                p.Id, p.Sku, p.NameAr, p.NameFr, p.NameEn, p.RentalOverdueMultiplier, p.RentalUnitPrice, p.RentalDamageUnitPrice))
            .ToListAsync(ct);

    /// <summary>One item's multiplier row — null override when it is on the company figure; null row when the item is unknown or deleted.</summary>
    public async Task<ProductOverdueMultiplierDto?> ProductMultiplierAsync(Guid productId, CancellationToken ct = default) =>
        await db.Products.AsNoTracking()
            .Where(p => p.Id == productId && !p.IsDeleted)
            .Select(p => new ProductOverdueMultiplierDto(
                p.Id, p.Sku, p.NameAr, p.NameFr, p.NameEn, p.RentalOverdueMultiplier, p.RentalUnitPrice, p.RentalDamageUnitPrice))
            .FirstOrDefaultAsync(ct);

    // ---------- contract ----------

    /// <remarks>
    /// With or without VAT is the seller's choice when the contract is made, as the customer asks for a tax invoice or
    /// not (owner, 2026-11-05); no permission is needed either way. Absent from the request, a contract is taxed, as
    /// every contract was before the choice; the screen always sends it.
    /// </remarks>
    public async Task<RentalResult<RentalContract>> CreateContractAsync(
        UpsertRentalContractRequest r, string? by, CancellationToken ct = default)
    {
        if (r.Lines is null || r.Lines.Count == 0)
            return RentalResult<RentalContract>.Invalid("A contract needs at least one line.");

        if (TaxModeOf(r.TaxMode, TaxMode.Taxed) is not TaxMode taxMode)
            return RentalResult<RentalContract>.Invalid("The tax mode is Taxed or Untaxed.");

        if (!await db.Customers.AnyAsync(c => c.Id == r.CustomerId && !c.IsDeleted, ct))
            return RentalResult<RentalContract>.NotFound("Customer not found.");
        if (!await db.Branches.AnyAsync(b => b.Id == r.BranchId && !b.IsDeleted, ct))
            return RentalResult<RentalContract>.NotFound("Branch not found.");

        var settings = await SettingsAsync(ct);
        if (DepositProblem(settings, r.DepositAmount) is string depositProblem)
            return RentalResult<RentalContract>.Invalid(depositProblem);

        // Answer 9: every contract raised from now on names one of the customer's sites.
        var site = await ResolveSiteAsync(r.CustomerId, r.SiteId, required: true, currentSiteId: null, ct);
        if (!site.Ok) return RentalResult<RentalContract>.Invalid(site.Message!);

        var signatory = await ResolveSignatoryAsync(r, underRule: true, ct);
        if (!signatory.Ok) return RentalResult<RentalContract>.Invalid(signatory.Message!);

        var wording = await ResolveWordingAsync(r.WordingKey, current: null, ct);
        if (!wording.Ok) return RentalResult<RentalContract>.Invalid(wording.Message!);

        var contract = new RentalContract
        {
            CustomerId = r.CustomerId,
            BranchId = r.BranchId,
            SignedAtUtc = r.SignedAtUtc ?? DateTime.UtcNow,
            Notes = r.Notes,
            CreatedBy = by,
            // Raised under the rule, so its first issue note needs a name. The one place this is
            // ever set: the migration leaves every earlier contract at false, and an edit of a
            // pre-release draft does not adopt the rule (see the entity comment).
            SignatoryRequired = true,
            WordingKey = wording.Value,
            // The minimum hire agreed with this contract; a later change of the setting never re-prices it.
            MinimumRentalDays = settings.MinimumDays,
            TaxMode = taxMode
        };
        // Snapshotted here and never recomputed from the customer: whoever represents the
        // company later, this contract keeps the person who signed it and the site it was for.
        ApplySignatory(contract, signatory.Value!);
        ApplySite(contract, site.Value);
        ApplyTruck(contract, r);

        var built = await BuildLinesAsync(contract.Id, r.Lines, taxMode, ct);
        if (!built.Ok) return RentalResult<RentalContract>.Invalid(built.Message!);

        await using var tx = await db.BeginWriteScopeAsync(ct);

        contract.Number = await ContractNumbers.NextAsync(db, r.BranchId, ct);
        // Recorded, not computed with (docs/07 §3.3): a figure signed on the paper and held on
        // the customer's behalf. Held() writes 0 while the switch is off — storing the requested
        // amount then would make it appear the day the switch is turned on, against contracts
        // that never agreed one. Nothing posts: collection and refund each wait on a money document.
        contract.DepositAmount = Engine.RentalDeposit.Held(settings, r.DepositAmount);

        contract.Lines = built.Value!;
        db.RentalContracts.Add(contract);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return RentalResult<RentalContract>.Success(contract);
    }

    /// <summary>
    /// Editing a contract, which is possible right up to the moment it is acted on and never
    /// afterwards.
    ///
    /// <para>
    /// The lock is enforced here rather than on a screen: once goods have gone out against
    /// these prices and quantities, an edit would silently re-price work already done and
    /// change what the customer signed. Everything that has to change after the first issue
    /// note is a document of its own — an extension, a return note.
    /// </para>
    /// </summary>
    public async Task<RentalResult<RentalContract>> UpdateContractAsync(
        Guid id, UpsertRentalContractRequest r, CancellationToken ct = default)
    {
        var contract = await db.RentalContracts.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (contract is null) return RentalResult<RentalContract>.NotFound("Contract not found.");
        if (NotOpen(contract) is string shut) return RentalResult<RentalContract>.Conflict(shut);
        if (r.Lines is null || r.Lines.Count == 0)
            return RentalResult<RentalContract>.Invalid("A contract needs at least one line.");

        // Asked of the engine, and cross-checked against the fact recorded on the row. They
        // agree by construction; if they ever stopped agreeing, refusing is the safe answer.
        var issued = await BatchesAsync(id, ct);
        if (Engine.RentalRules.IsLocked(issued) || contract.LockedAtUtc is not null)
            return RentalResult<RentalContract>.Conflict(
                "This contract has been issued against and is locked. Raise an extension or a return note instead.");

        // Whoever may edit the draft chooses with or without VAT (owner, 2026-11-05); absent keeps the stored mode.
        if (TaxModeOf(r.TaxMode, contract.TaxMode) is not TaxMode taxMode)
            return RentalResult<RentalContract>.Invalid("The tax mode is Taxed or Untaxed.");

        var settings = await SettingsAsync(ct);
        if (DepositProblem(settings, r.DepositAmount) is string depositProblem)
            return RentalResult<RentalContract>.Invalid(depositProblem);

        var built = await BuildLinesAsync(contract.Id, r.Lines, taxMode, ct);
        if (!built.Ok) return RentalResult<RentalContract>.Invalid(built.Message!);

        // A draft that names a site keeps naming one (it cannot be taken off), and a draft moved
        // to another customer must name one of that customer's; a contract raised before the site
        // rule may still be saved without one.
        var customerChanged = contract.CustomerId != r.CustomerId;
        var keepsSite = contract.SiteId is not null && !customerChanged;
        var site = await ResolveSiteAsync(
            r.CustomerId, r.SiteId, required: contract.SiteId is not null, currentSiteId: keepsSite ? contract.SiteId : null, ct);
        if (!site.Ok) return RentalResult<RentalContract>.Invalid(site.Message!);

        // Re-resolved against the request's — possibly new — customer, so a draft moved from
        // company A to company B never keeps A's representative. A blank result is allowed on a
        // draft. SignatoryRequired is left exactly as stored: a pre-release draft edited today
        // stays exempt (it was valid yesterday, and a phone-number edit must not turn into a
        // refusal), though the clerk may fill its signatory in and it is kept.
        var signatory = await ResolveSignatoryAsync(r, underRule: contract.SignatoryRequired, ct);
        if (!signatory.Ok) return RentalResult<RentalContract>.Invalid(signatory.Message!);

        var wording = await ResolveWordingAsync(r.WordingKey, current: contract.WordingKey, ct);
        if (!wording.Ok) return RentalResult<RentalContract>.Invalid(wording.Message!);

        await using var tx = await db.BeginWriteScopeAsync(ct);

        contract.CustomerId = r.CustomerId;
        contract.BranchId = r.BranchId;
        contract.SignedAtUtc = r.SignedAtUtc ?? contract.SignedAtUtc;
        contract.Notes = r.Notes;
        ApplySignatory(contract, signatory.Value!);
        if (site.Value is not null || customerChanged) ApplySite(contract, site.Value);
        ApplyTruck(contract, r);
        contract.WordingKey = wording.Value;
        contract.TaxMode = taxMode;
        // Still a draft, so the deposit moves with the prices; the lock above is what freezes it.
        contract.DepositAmount = Engine.RentalDeposit.Held(settings, r.DepositAmount);

        // Nothing has been issued, so no note, charge or movement points at the old lines and
        // replacing them wholesale is safe. After the first issue note this path is closed.
        db.RentalContractLines.RemoveRange(
            await db.RentalContractLines.Where(l => l.ContractId == id).ToListAsync(ct));
        db.RentalContractLines.AddRange(built.Value!);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return RentalResult<RentalContract>.Success(contract);
    }

    /// <summary>
    /// Who signed, and the identity the paper records for them — snapshotted onto the contract —
    /// and whether that is a person customer's agent rather than the customer himself.
    /// </summary>
    private sealed record SignatorySnapshot(
        string? Name, string? IdNumber, string? IdType, string? IdIssuer, DateOnly? IdIssuedOn, DateOnly? BirthDate,
        bool ByAgent = false);

    /// <summary>
    /// Who signs this contract, and the identity the paper records for them.
    ///
    /// <para>
    /// A person signs for themselves — the name comes from the customer and a different name in
    /// the request is ignored — <b>or through an agent</b> when the request says so (owner,
    /// 2026-10-06, reversing answer 11 for persons; the debt stays with the customer): the agent's
    /// name and identity are then taken as typed, nothing from the customer's record. A company
    /// signs through its representative: the current one when the request names nobody, or one
    /// named in its representative history, never anyone else (refused in words).
    /// </para>
    /// <para>
    /// The identity (document type, number, issuer, issue date, birth date) comes from the same
    /// record — the person, or the representative's row — and the request fills only what that
    /// record does not hold: a representative has no birth date on file, and a customer created
    /// before those fields existed has none of them. The first issue note refuses the contract
    /// while any of the five is still missing (<see cref="Engine.RentalRules.SignatoryIdentityMissing"/>).
    /// </para>
    /// <para>
    /// A customer whose kind is not set gives nothing to derive or check against, so the request
    /// is taken as typed; and a contract raised before the signatory rule
    /// (<paramref name="underRule"/> false) keeps the free text it was raised with.
    /// </para>
    /// </summary>
    private async Task<RentalResult<SignatorySnapshot>> ResolveSignatoryAsync(
        UpsertRentalContractRequest r, bool underRule, CancellationToken ct)
    {
        var resolved = await ResolveSignatoryFromRecordsAsync(r, underRule, ct);
        if (resolved.Value is not { } s) return resolved;
        // What will be stored must be well formed, whatever the rule asks for (owner, 2026-10-02):
        // the identity as it comes off the customer or representative row, not a typed value the
        // row overrides.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone()));
        return IdDocumentTypes.IdentityProblem(s.IdType, s.IdNumber, s.IdIssuer, s.IdIssuedOn, s.BirthDate, today) is string problem
            ? RentalResult<SignatorySnapshot>.Invalid(problem)
            : resolved;
    }

    private async Task<RentalResult<SignatorySnapshot>> ResolveSignatoryFromRecordsAsync(
        UpsertRentalContractRequest r, bool underRule, CancellationToken ct)
    {
        if (Engine.RentalRules.IdTypeProblem(r.SignatoryIdType) is not null)
            return RentalResult<SignatorySnapshot>.Invalid(
                "The identity document must be a national card (nationalCard), a driving licence (drivingLicence) or a passport (passport).");

        var typed = new SignatorySnapshot(
            TrimOrNull(r.SignatoryName), TrimOrNull(r.SignatoryIdNumber), TrimOrNull(r.SignatoryIdType),
            TrimOrNull(r.SignatoryIdIssuer), r.SignatoryIdIssuedOn, r.SignatoryBirthDate);

        var customer = await db.Customers.AsNoTracking()
            .Where(c => c.Id == r.CustomerId)
            .Select(c => new
            {
                c.Kind, c.FullName, c.IdDocumentNumber, c.IdDocumentType, c.IdDocumentIssuer, c.IdDocumentIssuedOn, c.BirthDate
            })
            .FirstOrDefaultAsync(ct);

        // A person is their own signatory; the record on file wins field by field.
        SignatorySnapshot Person() => new(
            TrimOrNull(customer!.FullName),
            TrimOrNull(customer.IdDocumentNumber) ?? typed.IdNumber,
            TrimOrNull(customer.IdDocumentType) ?? typed.IdType,
            TrimOrNull(customer.IdDocumentIssuer) ?? typed.IdIssuer,
            customer.IdDocumentIssuedOn ?? typed.IdIssuedOn,
            customer.BirthDate is DateTime born ? DateOnly.FromDateTime(born) : typed.BirthDate);

        if (customer?.Kind == CustomerKind.Person)
        {
            // The agent's own paper, as typed; a blank one is a valid draft, refused at its first
            // issue note.
            if (r.SignedByAgent) return RentalResult<SignatorySnapshot>.Success(typed with { ByAgent = true });
            // A pre-rule draft keeps a name typed on it; everything else is the person.
            if (!underRule && typed.Name is not null) return RentalResult<SignatorySnapshot>.Success(typed);
            return RentalResult<SignatorySnapshot>.Success(Person());
        }

        if (customer?.Kind != CustomerKind.Company)
            return RentalResult<SignatorySnapshot>.Success(typed);

        // Every representative the company has had: the open row first ("current" the way every
        // reader takes it: no end date, latest start first), then the rest newest first.
        var history = await db.CustomerRepresentatives.AsNoTracking()
            .Where(x => x.CustomerId == r.CustomerId)
            .Select(x => new { x.FullName, x.IdDocumentNumber, x.IdDocumentType, x.IdDocumentIssuer, x.IdDocumentIssuedOn, x.ToUtc, x.FromUtc })
            .ToListAsync(ct);
        var ordered = history.OrderBy(x => x.ToUtc is null ? 0 : 1).ThenByDescending(x => x.FromUtc).ToList();

        if (!underRule && typed.Name is not null) return RentalResult<SignatorySnapshot>.Success(typed);

        var chosen = typed.Name is null
            ? ordered.FirstOrDefault(x => x.ToUtc is null)
            : ordered.FirstOrDefault(x => string.Equals(x.FullName.Trim(), typed.Name, StringComparison.OrdinalIgnoreCase));

        if (chosen is null)
            return typed.Name is null
                // Nobody on file yet: a valid draft, refused at its first issue note.
                ? RentalResult<SignatorySnapshot>.Success(typed)
                : RentalResult<SignatorySnapshot>.Invalid(
                    $"{typed.Name} is not a representative of this company, current or past. Nobody signs on a customer's behalf: " +
                    "choose the company's representative, or record the new one in Customers first.");

        return RentalResult<SignatorySnapshot>.Success(new SignatorySnapshot(
            TrimOrNull(chosen.FullName),
            TrimOrNull(chosen.IdDocumentNumber) ?? typed.IdNumber,
            TrimOrNull(chosen.IdDocumentType) ?? typed.IdType,
            TrimOrNull(chosen.IdDocumentIssuer) ?? typed.IdIssuer,
            chosen.IdDocumentIssuedOn ?? typed.IdIssuedOn,
            // A representative's row holds no birth date, so this one is always the request's.
            typed.BirthDate));
    }

    private static void ApplySignatory(RentalContract contract, SignatorySnapshot signatory)
    {
        contract.SignatoryName = signatory.Name;
        contract.SignatoryIdNumber = signatory.IdNumber;
        contract.SignatoryIdType = signatory.IdType;
        contract.SignatoryIdIssuer = signatory.IdIssuer;
        contract.SignatoryIdIssuedOn = signatory.IdIssuedOn;
        contract.SignatoryBirthDate = signatory.BirthDate;
        contract.SignedByAgent = signatory.ByAgent;
    }

    /// <summary>
    /// The customer's site the contract is for (answer 9: mandatory, and one customer may have
    /// several). Only a site of that same customer, and an open one — unless it is the site the
    /// draft already names, which may have been closed since and stays valid on its own paper.
    /// </summary>
    private async Task<RentalResult<CustomerSite?>> ResolveSiteAsync(
        Guid customerId, Guid? siteId, bool required, Guid? currentSiteId, CancellationToken ct)
    {
        if (siteId is not Guid id)
            return required
                ? RentalResult<CustomerSite?>.Invalid(
                    "A contract names the site the goods go to: choose one of the customer's sites, or add a new one.")
                : RentalResult<CustomerSite?>.Success(null);

        var site = await db.CustomerSites.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (site is null || site.CustomerId != customerId)
            return RentalResult<CustomerSite?>.Invalid("That site is not one of this customer's sites.");
        if (!site.IsActive && site.Id != currentSiteId)
            return RentalResult<CustomerSite?>.Invalid($"The site {site.Name} is closed. Choose an open site, or reopen it first.");
        return RentalResult<CustomerSite?>.Success(site);
    }

    /// <summary>The site's name and address as they stand today, frozen on the paper.</summary>
    private static void ApplySite(RentalContract contract, CustomerSite? site)
    {
        contract.SiteId = site?.Id;
        contract.SiteName = site?.Name;
        contract.SiteAddress = TrimOrNull(site?.Address);
    }

    /// <summary>
    /// The named wording the contract prints (PinSay #302): null in the request keeps
    /// <paramref name="current"/> (an older client that does not send it), blank is the standard
    /// wording, and a key must be one of the company's contract wordings.
    /// </summary>
    private async Task<RentalResult<string?>> ResolveWordingAsync(string? requested, string? current, CancellationToken ct)
    {
        if (requested is null) return RentalResult<string?>.Success(current);
        var key = requested.Trim();
        if (key.Length == 0) return RentalResult<string?>.Success(null);
        var wordings = await db.Stores.AsNoTracking().Where(s => !s.IsDeleted).Select(s => s.ContractWordings).FirstOrDefaultAsync(ct);
        return (wordings ?? []).Any(w => w.Key == key)
            ? RentalResult<string?>.Success(key)
            : RentalResult<string?>.Invalid(
                "That contract wording does not exist: choose one of the wordings set under Company settings, Documents.");
    }

    /// <summary>The lorry named on the contract (answer 12); each note carries its own, editable.</summary>
    private static void ApplyTruck(RentalContract contract, UpsertRentalContractRequest r)
    {
        contract.TruckType = TrimOrNull(r.TruckType);
        contract.TruckNumber = TrimOrNull(r.TruckNumber);
        contract.TruckOwnerPhone = TrimOrNull(r.TruckOwnerPhone);
    }

    /// <summary>The first of the product's names that is not empty, in the order a document line prefers them.</summary>
    private static string FirstName(params string[] names) => names.FirstOrDefault(n => n.Length > 0) ?? string.Empty;

    private static string? TrimOrNull(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// A negative deposit is a typo, not a debt the company owes before anything happened. Only
    /// checked while the switch is on: off, the request's figure is ignored entirely, and a
    /// client that still sends one must not start failing the day the setting changes back.
    /// </summary>
    private static string? DepositProblem(Engine.RentalSettings settings, decimal requested) =>
        Engine.RentalDeposit.IsVisible(settings) && requested < 0
            ? "A deposit cannot be negative."
            : null;

    /// <summary>The request's tax mode, or <paramref name="absent"/> when it names none; null when it names neither.</summary>
    private static TaxMode? TaxModeOf(string? requested, TaxMode absent) =>
        string.IsNullOrWhiteSpace(requested) ? absent
        : string.Equals(requested.Trim(), nameof(TaxMode.Taxed), StringComparison.OrdinalIgnoreCase) ? TaxMode.Taxed
        : string.Equals(requested.Trim(), nameof(TaxMode.Untaxed), StringComparison.OrdinalIgnoreCase) ? TaxMode.Untaxed
        : null;

    private async Task<RentalResult<List<RentalContractLine>>> BuildLinesAsync(
        Guid contractId, IReadOnlyList<RentalContractLineRequest> requested, TaxMode taxMode, CancellationToken ct)
    {
        var settings = await SettingsAsync(ct);
        var regional = regionalSettings.Get();
        // Read here rather than carried on the engine's settings record: the engine does no
        // tax arithmetic, and a field it ignores would only invite someone to wire it wrongly.
        // An untaxed contract freezes no rate at all, not a rate nothing applies: every document
        // that follows reads the line's rate, and a rate stored there would be believed.
        var taxRate = taxMode == TaxMode.Untaxed ? 0m : (await db.Stores.AsNoTracking()
            .FirstOrDefaultAsync(x => !x.IsDeleted, ct))?.RentalTaxRate ?? 0m;

        var productIds = requested.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.Id) && !p.IsDeleted)
            .ToDictionaryAsync(p => p.Id, ct);

        var lines = new List<RentalContractLine>(requested.Count);
        for (var i = 0; i < requested.Count; i++)
        {
            var line = requested[i];
            if (!products.TryGetValue(line.ProductId, out var product))
                return RentalResult<List<RentalContractLine>>.Invalid($"Product {line.ProductId} not found.");

            // Derived once, here, from the multiplier in force at signing — the item's own if it
            // has one — and frozen. Changing the multiplier tomorrow never re-prices a signed
            // contract, which is the whole reason it is a multiplier and not a second price list
            // (§7 decision 4).
            var multiplier = settings.MultiplierFor(product.RentalOverdueMultiplier);
            var floor = Engine.RentalPricing.OverdueUnitPrice(regional, line.RentUnitPrice, multiplier);

            // The admin's figure is a floor for a negotiated price (docs/07 §3.4, accepted): a
            // manager may sign above it, never below. Refused with the figure named, so the
            // screen can say what the least acceptable price is rather than that "a rule" failed.
            //
            // Invariant on purpose: this sentence crosses the wire as one English string and the
            // screen shows it as it stands, so a server thread running under a French culture must
            // not turn the 1.5× multiplier into "1,5×" in a message nobody re-formats.
            if (Engine.RentalRules.OverduePriceProblem(line.OverdueUnitPrice, floor) is not null)
            {
                var plain = CultureInfo.InvariantCulture;
                return RentalResult<List<RentalContractLine>>.Invalid(
                    $"Line {i + 1} ({product.Sku}): the overdue price {line.OverdueUnitPrice?.ToString("0.##", plain)} is below the company floor of " +
                    $"{floor.ToString("0.##", plain)} ({multiplier.ToString("0.##", plain)}× the hire price). A negotiated overdue price may only go up.");
            }

            lines.Add(new RentalContractLine
            {
                ContractId = contractId,
                ProductId = product.Id,
                Sku = product.Sku,
                // The Arabic last: an article loaded from a branch's Arabic stock sheet has no other
                // name yet, and a contract line with no name at all prints as a blank row.
                NameSnapshot = FirstName(product.NameFr, product.NameEn, product.NameAr),
                // Answer 28: the Arabic name is printed beside the French, so it is frozen with it.
                NameArSnapshot = TrimOrNull(product.NameAr),
                Qty = line.Qty,
                DurationDays = line.DurationDays,
                RentUnitPrice = line.RentUnitPrice,
                // Answer 13: a fixed replacement value per item. A line that names none starts
                // from it; a figure typed on the line still wins, and is what gets signed.
                DamageUnitPrice = Engine.RentalRules.DamagePriceFor(line.DamageUnitPrice, product.RentalDamageUnitPrice),
                // The negotiated figure when one was signed (at or above the floor), else the floor itself.
                OverdueUnitPrice = line.OverdueUnitPrice ?? floor,
                // Frozen for the same reason the prices are: the customer signed a figure, and
                // a rate changed next quarter must not rewrite what they agreed to. Zero when
                // the company has set no rental tax rate, which is the default.
                TaxRate = taxRate,
                SortOrder = i
            });
        }

        for (var i = 0; i < requested.Count; i++)
        {
            if (requested[i].ParentLineIndex is not int parent) continue;
            if (parent < 0 || parent >= lines.Count || parent == i)
                return RentalResult<List<RentalContractLine>>.Invalid(
                    $"Line {i + 1} points at a parent line that is not on this contract.");
            // One level only. An accessory of an accessory has no period to inherit, because
            // the engine resolves a parent exactly once.
            if (requested[parent].ParentLineIndex is not null)
                return RentalResult<List<RentalContractLine>>.Invalid(
                    $"Line {i + 1} is an accessory of an accessory; attach it to the item itself.");
            lines[i].ParentLineId = lines[parent].Id;
        }

        foreach (var line in lines)
        {
            try { Engine.RentalRules.Validate(EngineLine(line)); }
            catch (ArgumentException ex) { return RentalResult<List<RentalContractLine>>.Invalid(ex.Message); }
        }

        return RentalResult<List<RentalContractLine>>.Success(lines);
    }

    // ---------- issue ----------

    /// <summary>
    /// Goods leaving for the site.
    ///
    /// <para>
    /// Three things happen at once and none of them can happen without the others: the quantity
    /// moves from available to on rent at the issuing branch, the customer is invoiced the full
    /// agreed period on what went out, and the contract locks.
    /// </para>
    /// </summary>
    public async Task<RentalResult<IssueOutcome>> IssueAsync(
        Guid contractId, IssueNoteRequest r, Guid? userId, string? userName, CancellationToken ct = default)
    {
        if (r.Lines is null || r.Lines.Count == 0)
            return RentalResult<IssueOutcome>.Invalid("An issue note needs at least one line.");

        // Answer 16: the driver and the truck are mandatory on the issue note, and the note is
        // refused without them — not warned about.
        if (!r.CustomerTransport && TransportProblem("An issue note", r.DriverName, r.DriverPhone, r.TruckNumber) is string transport)
            return RentalResult<IssueOutcome>.Invalid(transport);

        var docs = await LoadAsync(contractId, ct);
        if (docs is null) return RentalResult<IssueOutcome>.NotFound("Contract not found.");
        if (NotOpen(docs.Contract) is string shut) return RentalResult<IssueOutcome>.Conflict(shut);

        // The lock is where the paper matters. The first note is the one that locks the
        // contract, so it is the one refused while nobody is named as having signed; a contract
        // raised before the rule (SignatoryRequired false) is never refused, and a second note is
        // never asked. Before any stock is read and before the write scope: a refused note is not
        // a note, and moves nothing.
        if (docs.Contract.LockedAtUtc is null && Engine.RentalRules.SignatoryProblem(
                locked: false, docs.Contract.SignatoryRequired, docs.Contract.SignatoryName) is not null)
            return RentalResult<IssueOutcome>.Invalid(
                "This contract has no signatory. Open the contract, edit it and enter who signs before the first issue note.");

        // Then the rest of who signed (answer 10), at the same moment and on the same terms: the
        // first note only, and only on a contract raised under the rule. A person who signs
        // himself is not asked for it (owner, 2026-10-06): his paper is on his own record, and
        // printed from there when it holds one. His agent is asked, and a company's signatory.
        var signsHimself = !docs.Contract.SignedByAgent && await db.Customers.AsNoTracking()
            .AnyAsync(c => c.Id == docs.Contract.CustomerId && c.Kind == CustomerKind.Person, ct);
        IReadOnlyList<string> identityMissing = signsHimself
            ? []
            : Engine.RentalRules.SignatoryIdentityMissing(
                docs.Contract.LockedAtUtc is not null, docs.Contract.SignatoryRequired, IdentityOf(docs.Contract));
        if (identityMissing.Count > 0)
            return RentalResult<IssueOutcome>.Invalid(
                $"The signatory's identity is incomplete: {Words(identityMissing, IdentityWord)} {(identityMissing.Count == 1 ? "is" : "are")} missing. " +
                "Open the contract, edit it and complete the signatory's identity before the first issue note.");

        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == r.BranchId && !b.IsDeleted, ct);
        if (branch is null) return RentalResult<IssueOutcome>.NotFound("Branch not found.");

        var regional = regionalSettings.Get();
        var engineLines = docs.Lines.Select(EngineLine).ToList();
        var batches = Batches(docs, Zone());

        var note = new IssueNote
        {
            ContractId = contractId,
            BranchId = r.BranchId,
            IssuedAtUtc = r.IssuedAtUtc ?? DateTime.UtcNow,
            UserId = userId,
            UserName = userName,
            Note = r.Note,
            // Carried by the customer: nobody of the branch drove, so nothing typed is kept.
            CustomerTransport = r.CustomerTransport,
            DriverName = r.CustomerTransport ? null : TrimOrNull(r.DriverName),
            DriverPhone = r.CustomerTransport ? null : TrimOrNull(r.DriverPhone),
            TruckNumber = r.CustomerTransport ? null : TrimOrNull(r.TruckNumber)
        };
        note.PeriodStartsOn = StartsOn(note, Zone(), docs.DayCutoffHour);

        // Aggregated first: two lines of one note against the same contract line are one batch
        // as far as the arithmetic is concerned, and checking them separately would let the
        // pair exceed the contracted quantity one at a time.
        var wanted = new Dictionary<Guid, decimal>();
        foreach (var line in r.Lines)
            wanted[line.ContractLineId] = wanted.GetValueOrDefault(line.ContractLineId) + line.Qty;

        var periodRent = 0m;
        // Net per frozen rate rather than per line: tax is charged on the invoice total, the
        // same rule sales follow, so rounding each line first cannot drift from the figure the
        // customer is shown. Grouping keeps a document carrying two rates correct.
        var netByRate = new Dictionary<decimal, decimal>();
        foreach (var (lineId, qty) in wanted)
        {
            var contractLine = docs.Lines.FirstOrDefault(l => l.Id == lineId);
            if (contractLine is null) return RentalResult<IssueOutcome>.Invalid("A line on this note is not on the contract.");

            var engineLine = EngineLine(contractLine);
            var alreadyIssued = batches.Where(b => b.LineId == lineId).Sum(b => b.Qty);
            if (Engine.RentalRules.IssueProblem(engineLine, alreadyIssued, qty) is string problem)
                return RentalResult<IssueOutcome>.Invalid(problem switch
                {
                    "quantityRequired" => $"Line {contractLine.Sku}: a quantity above zero is required.",
                    "exceedsContracted" =>
                        $"Line {contractLine.Sku}: {alreadyIssued + qty:0.##} would exceed the contracted {contractLine.Qty:0.##}.",
                    _ => problem
                });

            note.Lines.Add(new IssueNoteLine { IssueNoteId = note.Id, ContractLineId = lineId, Qty = qty });

            // The full agreed period, on everything going out now. An accessory rents at zero
            // and contributes nothing here, which is exactly what "no value" means — it applies
            // to the rent line only, never to the compensation if it goes missing.
            var lineRent = Engine.RentalPricing.Amount(
                regional, contractLine.RentUnitPrice,
                Engine.RentalSettlement.DurationOf(engineLine, engineLines), qty);
            periodRent += lineRent;
            netByRate[contractLine.TaxRate] = netByRate.GetValueOrDefault(contractLine.TaxRate) + lineRent;
        }

        var periodTax = TaxOn(regional, netByRate);

        await using var tx = await db.BeginWriteScopeAsync(ct);

        var rows = new StockRows(db);
        foreach (var line in note.Lines)
        {
            var contractLine = docs.Lines.First(l => l.Id == line.ContractLineId);
            var stock = await rows.GetAsync(contractLine.ProductId, r.BranchId, ct);

            // The same check the stock module makes, for the same reason: you cannot hire out
            // what is not there, and a negative on-rent balance is a count nobody can reconcile.
            // (A sale is never blocked for a shortfall; an issue note is not a sale.)
            if (stock.Held(StockState.Available) < line.Qty)
                return RentalResult<IssueOutcome>.Conflict(
                    $"Line {contractLine.Sku}: only {stock.Held(StockState.Available):0.##} is available at {branch.NameFr}.");

            stock.Move(StockState.Available, StockState.OnRent, line.Qty);
            db.StockMovements.Add(new StockMovement
            {
                ProductId = contractLine.ProductId,
                BranchId = r.BranchId,
                // The company still owns it — it is the same merchandise in a different place,
                // and the buckets carry that. Only a write-off changes what is held.
                QtyDelta = 0,
                BalanceAfter = stock.StockQty,
                DocumentId = note.Id,
                Reason = "rental-issue",
                // The same goods hired out: no entry values it, so it carries no cost.
                Kind = StockMovementKind.RentalIssue,
                UserId = note.UserId,
                UserName = note.UserName,
                MovedQty = line.Qty
            });
        }
        await TouchProductsAsync(note.Lines.Select(l => docs.Lines.First(x => x.Id == l.ContractLineId).ProductId), ct);

        db.IssueNotes.Add(note);
        note.Number = Numbered("ISS", await db.IssueNotes.CountAsync(ct) + 1);
        StampDocumentNumber(note.Id, note.Number);

        // Available → on rent moves no value at all, so StockPosting returns nothing for it and
        // the only entry this note raises is the invoice.
        if (RentalPosting.Issue(
                note.Id, r.BranchId, docs.Contract.CustomerId, note.Number, periodRent, userName,
                StartOfDayUtc(note.PeriodStartsOn.Value, Zone()), periodTax) is PostingDraft invoice)
            await ledger.PostAsync(invoice, ct);

        // Under the version lock: a till pushing a credit sale may be adding to this debt.
        var customer = await db.LockedAsync<Customer>(docs.Contract.CustomerId, ct);
        // The whole invoice, tax included — the same figure the ledger bills to 411. Raising the
        // balance by the net alone left the denominated balance short of the customer's real
        // trade debt by exactly the tax, on every hire, wherever the company charges tax on
        // rent; and that balance is the figure the credit-limit warning reads.
        if (customer is not null && periodRent + periodTax != 0)
        {
            customer.Balance += periodRent + periodTax;
            await db.TouchAsync(customer, ct);
        }

        // The fact, recorded: from here the contract is a signed document that has been acted
        // on, and the only way to change anything about it is another document.
        docs.Contract.LockedAtUtc ??= DateTime.UtcNow;
        // Goods are out again, so they are no longer all back: paying must not close the contract
        // while this batch is on site (answers 15, 22).
        docs.Contract.GoodsBackAtUtc = null;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return RentalResult<IssueOutcome>.Success(new IssueOutcome(note, periodRent));
    }

    // ---------- return ----------

    /// <summary>
    /// Goods coming back, in whatever condition they come back in.
    ///
    /// <para>
    /// The quantities decide where they land: sound units are available again, damaged but
    /// repairable ones go to maintenance, wrecks become scrap, and units that never came back
    /// are written off — at the branch that issued them, because they never arrived anywhere
    /// else.
    /// </para>
    /// <para>
    /// The note is taken at the <b>contract's branch</b> and nowhere else (the owner, 2026-09-30);
    /// another branch is refused in words. Goods an Admin issued from a different branch still
    /// come back to the contract's, and that difference is recorded as an implicit transfer
    /// (§7 decision 2), or one branch ends up holding stock it never received.
    /// </para>
    /// </summary>
    public async Task<RentalResult<ReturnOutcome>> ReturnAsync(
        Guid contractId, ReturnNoteRequest r, Guid? userId, string? userName, CancellationToken ct = default)
    {
        if (r.Lines is null || r.Lines.Count == 0)
            return RentalResult<ReturnOutcome>.Invalid("A return note needs at least one line.");

        // Answer 16 again: the owner wrote "إجباري" beside these three on his own return note.
        if (!r.CustomerTransport && TransportProblem("A return note", r.DriverName, r.DriverPhone, r.TruckNumber) is string transport)
            return RentalResult<ReturnOutcome>.Invalid(transport);

        var docs = await LoadAsync(contractId, ct);
        if (docs is null) return RentalResult<ReturnOutcome>.NotFound("Contract not found.");
        if (NotOpen(docs.Contract) is string shut) return RentalResult<ReturnOutcome>.Conflict(shut);

        var branch = await db.Branches.FirstOrDefaultAsync(b => b.Id == r.BranchId && !b.IsDeleted, ct);
        if (branch is null) return RentalResult<ReturnOutcome>.NotFound("Branch not found.");

        // The owner, 2026-09-30: "the return is to the same branch, then it is transferred to
        // another branch". Goods come back to the contract's branch, whoever takes the note; moving
        // them on is an ordinary stock transfer afterwards, never a side effect of the return.
        if (r.BranchId != docs.Contract.BranchId)
        {
            var home = await db.Branches.AsNoTracking().Where(b => b.Id == docs.Contract.BranchId)
                .Select(b => b.NameFr).FirstOrDefaultAsync(ct);
            return RentalResult<ReturnOutcome>.Invalid(string.Format(ReturnAtContractBranchMessage, home));
        }

        var zone = Zone();
        var regional = regionalSettings.Get();
        var returnedAt = r.ReturnedAtUtc ?? DateTime.UtcNow;

        // Notes are settled in date order and FIFO consumes the batches as it goes, so a note
        // slipped in behind one already taken would re-decide charges the customer has already
        // been handed. Append-only, like every other document in this system.
        var latest = docs.ReturnNotes.Count == 0 ? (DateTime?)null : docs.ReturnNotes.Max(n => n.ReturnedAtUtc);
        if (latest is DateTime last && returnedAt < last)
            return RentalResult<ReturnOutcome>.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"A return note cannot be dated before one already taken ({last:yyyy-MM-dd})."));

        // The return day (answer 6): counted by default for goods back at or after the company's
        // cutoff hour, not before it — and the branch manager may decide either way, in writing
        // when he departs from the rule. The decision in force is stored on the note, so a later
        // change of the hour never re-decides a return already priced.
        var localReturn = Engine.RentalDays.LocalTime(returnedAt, zone);
        var byDefault = Engine.RentalDays.CountsReturnDayByDefault(localReturn, docs.DayCutoffHour);
        if (Engine.RentalDays.ReturnDayProblem(r.CountReturnDay, byDefault, r.ReturnDayReason) is not null)
            return RentalResult<ReturnOutcome>.Invalid(
                string.Create(CultureInfo.InvariantCulture, $"For goods back at {localReturn:HH:mm} the rule {(byDefault ? "counts" : "does not count")} the return day. ") +
                $"{(byDefault ? "Not counting" : "Counting")} it is the branch manager's decision: write the reason for it.");
        var countReturnDay = r.CountReturnDay ?? byDefault;

        var note = new ReturnNote
        {
            ContractId = contractId,
            BranchId = r.BranchId,
            ReturnedAtUtc = returnedAt,
            UserId = userId,
            UserName = userName,
            Note = r.Note,
            CustomerTransport = r.CustomerTransport,
            DriverName = r.CustomerTransport ? null : TrimOrNull(r.DriverName),
            DriverPhone = r.CustomerTransport ? null : TrimOrNull(r.DriverPhone),
            TruckNumber = r.CustomerTransport ? null : TrimOrNull(r.TruckNumber),
            CountReturnDay = countReturnDay,
            ReturnDayReason = TrimOrNull(r.ReturnDayReason),
            // Named only when somebody decided; the rule deciding on its own is nobody's decision.
            ReturnDayDecidedBy = r.CountReturnDay is null ? null : userName
        };

        var batches = Batches(docs, zone);
        var priorEntries = Entries(docs, zone);
        var requestedEntries = new List<Engine.ReturnEntry>();

        foreach (var line in r.Lines)
        {
            var contractLine = docs.Lines.FirstOrDefault(l => l.Id == line.ContractLineId);
            if (contractLine is null) return RentalResult<ReturnOutcome>.Invalid("A line on this note is not on the contract.");

            var usage = SameDayUsage.LegacyUnknown;
            if (!string.IsNullOrWhiteSpace(line.SameDayUsage)
                && (!Enum.TryParse(line.SameDayUsage, true, out usage)
                    || usage is not (SameDayUsage.Used or SameDayUsage.NotUsed)))
                return RentalResult<ReturnOutcome>.Invalid("sameDayUsageInvalid");
            // A loss is not a physical return, so it must never inherit a usage decision (or
            // the legacy-row marker, which intentionally preserves old mixed-condition pricing).
            if (line.QtyOk + line.QtyPartialDamage + line.QtyTotalDamage == 0)
                usage = SameDayUsage.NotApplicable;

            var savedLine = new ReturnNoteLine
            {
                Id = Guid.NewGuid(), ReturnNoteId = note.Id, ContractLineId = line.ContractLineId,
                QtyOk = line.QtyOk, QtyPartialDamage = line.QtyPartialDamage,
                MaintenanceValue = line.MaintenanceValue, QtyTotalDamage = line.QtyTotalDamage,
                QtyMissing = line.QtyMissing, ReportedOnUtc = line.ReportedOnUtc,
                SameDayUsage = usage,
                UsageSelectedByUserId = usage is SameDayUsage.Used or SameDayUsage.NotUsed ? userId : null,
                UsageSelectedAtUtc = usage is SameDayUsage.Used or SameDayUsage.NotUsed ? DateTime.UtcNow : null
            };

            var entry = new Engine.ReturnEntry(
                note.Id, line.ContractLineId, Engine.RentalPricing.LocalDay(returnedAt, zone),
                line.QtyOk, line.QtyPartialDamage, line.MaintenanceValue, line.QtyTotalDamage, line.QtyMissing,
                line.ReportedOnUtc is DateTime reported ? Engine.RentalPricing.LocalDay(reported, zone) : null,
                countReturnDay, ReturnLineId: savedLine.Id, SameDayUsage: usage);

            var issuedQty = batches.Where(b => b.LineId == line.ContractLineId).Sum(b => b.Qty);
            var settledQty = priorEntries.Concat(requestedEntries).Where(e => e.LineId == line.ContractLineId).Sum(e => e.SettledQty);

            if (Engine.RentalRules.ReturnProblem(entry, issuedQty, settledQty) is string problem)
                return RentalResult<ReturnOutcome>.Invalid(problem switch
                {
                    "negativeQuantity" => $"Line {contractLine.Sku}: a returned quantity cannot be negative.",
                    "quantityRequired" => $"Line {contractLine.Sku}: nothing was returned or written off.",
                    "negativeMaintenance" => $"Line {contractLine.Sku}: a repair cannot cost a negative amount.",
                    "maintenanceNeedsDamagedUnits" =>
                        $"Line {contractLine.Sku}: a repair charge needs the units it repairs.",
                    "exceedsIssued" =>
                        $"Line {contractLine.Sku}: {settledQty + entry.SettledQty:0.##} settled would exceed the {issuedQty:0.##} issued.",
                    _ => problem
                });

            // Kept even when it will be ignored — a report dated after settlement remains auditable.
            note.Lines.Add(savedLine);
            requestedEntries.Add(entry);
        }

        // Validate the explicit decision against actual issue and return dates in the company
        // zone. The cutoff-adjusted pricing start is intentionally irrelevant here.
        var returnedDay = Engine.RentalPricing.LocalDay(returnedAt, zone);
        foreach (var line in note.Lines.Where(l => l.QtyOk + l.QtyPartialDamage + l.QtyTotalDamage > 0))
        {
            var contractLine = docs.Lines.Single(l => l.Id == line.ContractLineId);
            var allocation = Engine.RentalSettlement.AllocateLine(
                EngineLine(contractLine), Engine.RentalSettlement.DurationOf(EngineLine(contractLine), docs.Lines.Select(EngineLine).ToList()),
                batches.Where(b => b.LineId == line.ContractLineId).ToList(),
                priorEntries.Concat(requestedEntries).Where(e => e.LineId == line.ContractLineId).ToList(),
                Engine.RentalSettlement.ExtensionsFor(EngineLine(contractLine), Extensions(docs, zone)),
                regional, docs.Contract.MinimumRentalDays, allowMissingUsage: true)
                .Where(a => a.Return.ReturnLineId == line.Id && a.Condition != RentalReturnCondition.Missing)
                .ToList();
            var needsUsage = allocation.Any(a => a.Batch.ActualIssuedOn == returnedDay);
            if (needsUsage && line.SameDayUsage is not (SameDayUsage.Used or SameDayUsage.NotUsed))
                return RentalResult<ReturnOutcome>.Invalid(
                    "Same-day returned goods require an explicit Used or NotUsed decision.");
            if (!needsUsage) line.SameDayUsage = SameDayUsage.NotApplicable;
        }

        // Settle twice: once as things stood, once with this note included. The engine reports
        // unused rent per line as a running total over every return, and what belongs on this
        // note — and therefore what the customer is given back today — is the difference.
        var before = Engine.RentalSettlement.Settle(Input(docs, zone), regional);
        docs.ReturnNotes.Add(note);
        var after = Engine.RentalSettlement.Settle(Input(docs, zone), regional);

        // Back after the end of the period (the owner, 2026-10-08): the screen asks whether to
        // charge the late days, and only the answer travels — the days and the money are the
        // engine's, worked out above. Charging is the rule (answer 7) and no answer keeps it;
        // waiving is the exception, written down with the name of whoever answered (answer 55).
        // A note that is not late has nothing to answer, and an answer sent with it is not kept.
        // The third answer (the owner, 2026-10-22) is yes less a deduction, in days or as an
        // amount, checked against this note's own late days and late total — both read off the
        // settlement above, before any answer — and never more than either.
        var noteCharges = after.Charges.Where(c => c.ReturnNoteId == note.Id).ToList();
        if (r.ChargeOverdue is bool chargeLate && Engine.RentalLateReturn.IsLate(noteCharges))
        {
            var lateAmount = Engine.RentalLateReturn.LateAmount(noteCharges);
            if (Engine.RentalLateReturn.DeductionProblem(chargeLate, r.LateDaysWaived, r.LateAmountWaived,
                    Engine.RentalLateReturn.LateDays(noteCharges), lateAmount, regional) is not null)
                return RentalResult<ReturnOutcome>.Invalid(
                    "A deduction from the late days is in days or as an amount, never both, and never more than " +
                    "the late days or what they come to.");
            var deducts = chargeLate && (r.LateDaysWaived is not null || r.LateAmountWaived is not null);
            if (Engine.RentalLateReturn.WaiverProblem(chargeLate, r.LateWaiverReason, deducts) is not null)
                return RentalResult<ReturnOutcome>.Invalid(
                    "These goods are back after the end of their period. Not charging the late days is the branch " +
                    "manager's decision: write the reason for it.");
            note.WaiveOverdue = !chargeLate;
            note.LateDaysWaived = deducts ? r.LateDaysWaived : null;
            note.LateAmountWaived = deducts ? r.LateAmountWaived : null;
            note.LateAmountFull = deducts && r.LateAmountWaived is not null ? lateAmount : null;
            note.LateWaiverReason = chargeLate && !deducts ? null : TrimOrNull(r.LateWaiverReason);
            note.LateDecidedBy = userName;
            if (note.WaiveOverdue || deducts) after = Engine.RentalSettlement.Settle(Input(docs, zone), regional);
        }

        var newAllocations = new List<ReturnNoteLineAllocation>();
        foreach (var entry in requestedEntries)
        {
            var line = note.Lines.Single(l => l.Id == entry.ReturnLineId);
            var contractLine = docs.Lines.Single(l => l.Id == line.ContractLineId);
            var engineLine = EngineLine(contractLine);
            var portions = Engine.RentalSettlement.AllocateLine(engineLine,
                Engine.RentalSettlement.DurationOf(engineLine, docs.Lines.Select(EngineLine).ToList()),
                batches.Where(b => b.LineId == line.ContractLineId).ToList(),
                Entries(docs, zone).Where(e => e.LineId == line.ContractLineId).ToList(),
                Engine.RentalSettlement.ExtensionsFor(engineLine, Extensions(docs, zone)), regional, docs.Contract.MinimumRentalDays)
                .Where(a => a.Return.ReturnLineId == line.Id);
            foreach (var portion in portions)
            {
                var actualRent = portion.ActualRent;
                var allocation = new ReturnNoteLineAllocation
                {
                    ReturnNoteLineId = line.Id, ReturnNoteLine = line,
                    IssueNoteId = portion.Batch.IssueNoteId,
                    ContractLineId = line.ContractLineId, Qty = portion.Qty, Condition = portion.Condition,
                    ChargeableDays = portion.Stay.ChargedDays, UnusedRentNet = portion.UnusedRent,
                    ActualRentNet = actualRent, DailyUnitRate = contractLine.RentUnitPrice,
                    TaxRate = contractLine.TaxRate,
                    IssuedAtUtc = docs.IssueNotes.Single(n => n.Id == portion.Batch.IssueNoteId).IssuedAtUtc,
                    ReturnedAtUtc = returnedAt,
                    SameDayUsage = portion.Condition == RentalReturnCondition.Missing
                        ? SameDayUsage.NotApplicable : portion.Return.SameDayUsage
                };
                newAllocations.Add(allocation);
            }
        }

        var unusedRent = Math.Max(0m, after.UnusedRent - before.UnusedRent);

        await using var tx = await db.BeginWriteScopeAsync(ct);

        var charges = StoreCharges(docs.Contract, after, note.Id);
        var transfers = await MoveReturnedStockAsync(docs, note, zone, ct);
        if (!transfers.Ok) return RentalResult<ReturnOutcome>.Conflict(transfers.Message!);

        // Under the version lock: a till pushing a credit sale may be adding to this debt.
        var customer = await db.LockedAsync<Customer>(docs.Contract.CustomerId, ct);
        var owed = charges.Where(c => c.IsPosted).Sum(c => c.Amount);

        // A customer who still owes more than the refund has it taken off the debt and no money
        // leaves; only the excess is a voucher. Recorded rather than paid: this module raises no
        // payment, exactly as receiving raises none to a supplier, so the figure is visible and
        // the act of paying it stays a deliberate one. What this same note charges (a unit that
        // never came back, a repair, overdue days) is owed too (answer 27): leaving it out would
        // print a voucher for more than the account holds in the customer's favour.
        // Tax on what this note charges, less the tax on the advance being given back. A large
        // early return makes it negative, which is right: refunding an invoice refunds its tax.
        var netOnReturn = new Dictionary<decimal, decimal>();
        foreach (var charge in charges.Where(c => c.IsPosted))
        {
            var rate = docs.Lines.FirstOrDefault(l => l.Id == charge.ContractLineId)?.TaxRate ?? 0m;
            netOnReturn[rate] = netOnReturn.GetValueOrDefault(rate) + charge.Amount;
        }
        foreach (var group in newAllocations.Where(a => a.UnusedRentNet > 0).GroupBy(a => a.TaxRate))
            netOnReturn[group.Key] = netOnReturn.GetValueOrDefault(group.Key) - group.Sum(a => a.UnusedRentNet);

        var returnTax = TaxOn(regional, netOnReturn);
        var unusedByRate = newAllocations.Where(a => a.UnusedRentNet > 0)
            .GroupBy(a => a.TaxRate)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.UnusedRentNet));
        var unusedTax = TaxOn(regional, unusedByRate);
        var grossUnusedRelief = unusedRent + unusedTax;
        // Match the tax-inclusive customer balance movement below. Existing unpaid amounts,
        // this return's charges, and their tax are settled before any unused advance is refunded.
        var relief = Engine.RentalSettlement.Relieve(grossUnusedRelief,
            (customer?.Balance ?? 0m) + owed + returnTax + unusedTax);
        note.UnusedRent = unusedRent; // keep the rental amount net; VAT remains a separate ledger component
        note.ReliefDebtReduction = relief.DebtReduction;
        note.ReliefPaymentVoucher = relief.PaymentVoucher;

        db.ReturnNotes.Add(note);
        note.Number = Numbered("RET", await db.ReturnNotes.CountAsync(ct) + 1);
        StampDocumentNumber(note.Id, note.Number);
        db.RentalCharges.AddRange(charges);
        await db.SaveChangesAsync(ct);
        db.ReturnNoteLineAllocations.AddRange(newAllocations);
        // Maintenance MS-03 (docs/maintenance/plan.md §2.2): one draft repair job per damaged line, in this transaction —
        // only with the module on and that option chosen. By default it reads the settings and adds nothing.
        await Maintenance.MaintenanceJobs.DraftForReturnNoteAsync(db, note, docs.Lines, ct);

        if (RentalPosting.Return(
                note.Id, r.BranchId, docs.Contract.CustomerId, note.Number,
                charges.Where(c => c.IsPosted).Select(c => new ChargeAmount(c.Kind, c.Amount, note.Number)),
                unusedRent, userName, returnTax) is PostingDraft settlement)
            await ledger.PostAsync(settlement, ct);

        // Charged less refunded, plus the net tax on both — again the figure posted to 411.
        if (customer is not null && owed - unusedRent + returnTax != 0)
        {
            customer.Balance += owed - unusedRent + returnTax;
            await db.TouchAsync(customer, ct);
        }

        // Issued = returned + settled as lost or damaged, on every line. Quantities never issued
        // have no bearing on it, and a contract that has issued nothing has not started. The
        // engine states the whole rule; nothing here re-decides it. The goods being back is not
        // the close (answers 15, 22): the contract closes once it is also paid, which this note
        // may itself bring about by giving back an advance larger than what is owed.
        if (after.IsClosed) docs.Contract.GoodsBackAtUtc ??= DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        var closed = after.IsClosed && await RentalClosure.TryCloseAsync(db, docs.Contract.Id, ct);
        if (closed) await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return RentalResult<ReturnOutcome>.Success(new ReturnOutcome(note, charges, transfers.Value!, closed));
    }

    /// <summary>One batch of a return note as it posts: <see cref="ValuedBy"/> is the row whose value its entries carry.</summary>
    private sealed record ReturnPosting(
        Guid From, Guid To, StockState State, decimal Qty, decimal Reference, string? ProductSku, string LineSku, StockMovement ValuedBy,
        Guid ProductId);

    /// <summary>
    /// Everything this return note does to stock, batch by batch.
    ///
    /// <para>
    /// FIFO decides which issue batch each returned unit came off, and therefore which branch it
    /// left from — the same match the money is worked out with, asked of the same engine, so the
    /// quantities and the charges can never disagree about where a unit came from.
    /// </para>
    /// <para>
    /// Every row is built first, then the note's rows are valued together — at the cost price, or, once sales are
    /// costed at the weighted average (C-01), as the shadow values them — and only then posted, in the order they
    /// always were: lost goods at the value their row took out of the issuing branch, goods going to another branch or
    /// into scrap at what they left the issuing branch at.
    /// </para>
    /// </summary>

    private async Task<RentalResult<List<ImplicitTransfer>>> MoveReturnedStockAsync(
        Documents docs, ReturnNote note, TimeZoneInfo zone, CancellationToken ct)
    {
        var transfers = new List<ImplicitTransfer>();
        var planned = new List<PlannedMovement>();
        var postings = new List<ReturnPosting>();
        var rows = new StockRows(db);
        var branchOfNote = docs.IssueNotes.ToDictionary(n => n.Id, n => n.BranchId);
        var branchNames = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.NameFr, ct);

        foreach (var line in note.Lines)
        {
            var contractLine = docs.Lines.First(l => l.Id == line.ContractLineId);
            var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == contractLine.ProductId, ct);

            var batches = Batches(docs, zone).Where(b => b.LineId == line.ContractLineId).ToList();
            var entries = Entries(docs with { ReturnNotes = docs.ReturnNotes.Append(note).ToList() }, zone)
                .Where(e => e.LineId == line.ContractLineId)
                .OrderBy(e => e.ReturnedOn).ToList();
            var matches = Engine.RentalFifo.Match(batches, entries.Select(e => e.SettledQty));
            var index = entries.FindIndex(e => e.ReturnLineId == line.Id);
            var takes = index >= 0 ? matches[index].Takes : [];

            foreach (var (issueNoteId, state, qty) in Allocate(takes, line))
            {
                var from = branchOfNote.GetValueOrDefault(issueNoteId, note.BranchId);
                // A unit that never came back never arrived at the branch taking the note, so it
                // is written off where it was issued. Transferring it first would record goods
                // arriving somewhere they were never seen.
                var to = state == StockState.WrittenOff ? from : note.BranchId;

                var source = await rows.GetAsync(contractLine.ProductId, from, ct);
                if (source.Held(StockState.OnRent) < qty)
                    return RentalResult<List<ImplicitTransfer>>.Conflict(
                        $"Line {contractLine.Sku}: only {source.Held(StockState.OnRent):0.##} is on rent at " +
                        $"{branchNames.GetValueOrDefault(from, from.ToString())}.");

                var reference = product?.CostPrice ?? 0m;
                StockMovement valuedBy;

                if (from == to)
                {
                    source.Move(StockState.OnRent, state, qty);
                    valuedBy = db.StockMovements.Add(new StockMovement
                    {
                        ProductId = contractLine.ProductId,
                        BranchId = from,
                        QtyDelta = state == StockState.WrittenOff ? -qty : 0,
                        BalanceAfter = source.StockQty,
                        DocumentId = note.Id,
                        Reason = "rental-return",
                        Kind = StockMovementKind.RentalReturn, UnitCost = product?.CostPrice,
                        UserId = note.UserId, UserName = note.UserName, MovedQty = qty
                    }).Entity;
                    planned.Add(new PlannedMovement(valuedBy, reference));
                }
                else
                {
                    // The implicit transfer §7 decision 2 asks for: the goods leave the branch
                    // that hired them out and arrive at the one that took them back. Without it
                    // the issuing branch keeps an on-rent balance for goods that are standing in
                    // another branch's yard, and the two drift apart with nothing to explain it.
                    var destination = await rows.GetAsync(contractLine.ProductId, to, ct);
                    source.Add(StockState.OnRent, -qty);
                    destination.Add(state, qty);

                    valuedBy = db.StockMovements.Add(new StockMovement
                    {
                        ProductId = contractLine.ProductId, BranchId = from, RelatedBranchId = to,
                        QtyDelta = -qty, BalanceAfter = source.StockQty,
                        DocumentId = note.Id, Reason = "rental-return-transfer",
                        Kind = StockMovementKind.RentalReturnTransfer, UnitCost = product?.CostPrice,
                        UserId = note.UserId, UserName = note.UserName, MovedQty = qty
                    }).Entity;
                    var arriving = db.StockMovements.Add(new StockMovement
                    {
                        ProductId = contractLine.ProductId, BranchId = to, RelatedBranchId = from,
                        QtyDelta = qty, BalanceAfter = destination.StockQty,
                        DocumentId = note.Id, Reason = "rental-return-transfer",
                        Kind = StockMovementKind.RentalReturnTransfer, UnitCost = product?.CostPrice,
                        UserId = note.UserId, UserName = note.UserName, MovedQty = qty
                    }).Entity;
                    planned.Add(new PlannedMovement(valuedBy, reference));
                    planned.Add(new PlannedMovement(arriving, reference));

                    transfers.Add(new ImplicitTransfer(contractLine.ProductId, from, to, qty, state));
                }
                postings.Add(new ReturnPosting(from, to, state, qty, reference, product?.Sku, contractLine.Sku, valuedBy, contractLine.ProductId));
            }
        }

        // Valued once every row of the note is known, so a second row of a product sees what the first took out: at
        // the cost price, or, once sales are costed at the weighted average (C-01), as the shadow values the rows.
        var costs = await new LiveSaleCosting(db).MovementCostsOrFallbackAsync(planned, logger, ct);
        if (costs is not null)
            foreach (var plan in planned)
            {
                plan.Movement.UnitCost = costs[plan.Movement.Id].UnitCost;
                plan.Movement.CostBasis = costs[plan.Movement.Id].Basis;
            }

        // REN-12 (Phase M7a): while the fixed-asset register holds the hire equipment, its units move between the branches'
        // pools with their cost and depreciation, and wrecked or lost units leave their pool at their net book value. Off
        // (the default), or for any other product, the entries below are today's.
        var assets = new FixedAssetBook(db);
        foreach (var e in postings)
        {
            if (await assets.HoldsAsync(e.ProductId, ct))
            {
                var asset = new FixedAssetEvent(e.ProductId, e.From, e.Qty, e.ProductSku, note.Id, note.Number,
                    $"rental return {e.LineSku}", note.UserName);
                if (e.From != e.To && await assets.TransferAsync(asset, e.To, ct) is PostingDraft moved)
                    await ledger.PostAsync(moved, ct);
                if ((await assets.StateChangeAsync(asset with { BranchId = e.To }, StockState.OnRent, e.State, ct)).Draft is PostingDraft left)
                    await ledger.PostAsync(left, ct);
                continue;
            }

            // Lost goods leave at the value their row took out; goods moving to another branch, or into scrap, go at
            // what they left the issuing branch at.
            var value = costs?.GetValueOrDefault(e.ValuedBy.Id)?.Value ?? e.Qty * e.Reference;
            if (e.From != e.To)
            {
                // Marked inter-branch so consolidation drops it: goods moving inside the
                // company are real at each branch and nothing at all for the group.
                await ledger.PostAsync(StockPosting.Transfer(
                    Guid.NewGuid(), e.From, e.To,
                    branchNames.GetValueOrDefault(e.From, ""), branchNames.GetValueOrDefault(e.To, ""),
                    value, e.ProductSku), ct);
            }

            // Available, on rent and in maintenance are the same asset in different places
            // and post nothing. Scrap is a different asset and a write-off is not an asset
            // at all — StockPosting decides which, exactly as it does for the stock module.
            if (StockPosting.StateChange(
                    Guid.NewGuid(), e.To, StockState.OnRent, e.State, value, e.ProductSku,
                    $"rental return {e.LineSku}") is PostingDraft draft)
                await ledger.PostAsync(draft, ct);
        }

        await TouchProductsAsync(note.Lines.Select(l => docs.Lines.First(x => x.Id == l.ContractLineId).ProductId), ct);
        return RentalResult<List<ImplicitTransfer>>.Success(transfers);
    }

    /// <summary>
    /// A till sells from the same available stock an exit note takes and a return gives back (owner,
    /// 2026-10-06: one stock), and a till's pull watches the product's own version — so every rental
    /// stock move bumps it, as an adjustment or a transfer does. Otherwise the till keeps showing
    /// what was there before the goods went out. The caller saves.
    /// </summary>
    private async Task TouchProductsAsync(IEnumerable<Guid> productIds, CancellationToken ct)
    {
        var ids = productIds.Distinct().ToList();
        foreach (var product in await db.Products.Where(p => ids.Contains(p.Id)).ToListAsync(ct))
            await db.TouchAsync(product, ct);
    }

    /// <summary>
    /// Spreads one return line's four condition quantities over the batches FIFO matched to it,
    /// so each unit is put back at the branch it actually left from.
    ///
    /// <para>
    /// Sound first, then repairable, then wrecked, then missing. The order only shows when one
    /// note mixes conditions <i>and</i> draws on batches issued at different branches, and any
    /// fixed order is defensible there — what matters is that it is fixed, so two people
    /// counting the same pile move the same stock.
    /// </para>
    /// <para>
    /// Quantity the match could not place is quantity that never went out. It moves nothing,
    /// because there is nothing of it anywhere; the engine reports it as over-returned.
    /// </para>
    /// </summary>
    private static IEnumerable<(Guid IssueNoteId, StockState State, decimal Qty)> Allocate(
        IReadOnlyList<Engine.BatchTake> takes, ReturnNoteLine line)
    {
        (StockState State, decimal Qty)[] buckets =
        [
            (StockState.Available, line.QtyOk),
            (StockState.Maintenance, line.QtyPartialDamage),
            (StockState.Scrap, line.QtyTotalDamage),
            (StockState.WrittenOff, line.QtyMissing)
        ];

        var index = 0;
        var remaining = takes.Count > 0 ? takes[0].Qty : 0m;

        foreach (var (state, wanted) in buckets)
        {
            var left = wanted;
            while (left > 0 && index < takes.Count)
            {
                if (remaining <= 0)
                {
                    index++;
                    if (index >= takes.Count) break;
                    remaining = takes[index].Qty;
                    continue;
                }
                var take = Math.Min(left, remaining);
                yield return (takes[index].Batch.IssueNoteId, state, take);
                remaining -= take;
                left -= take;
            }
        }
    }

    /// <summary>
    /// Freezes the charges this return note produced.
    ///
    /// <para>
    /// ⚠ Rent inside the agreed period is <b>recorded and not posted</b>. It was invoiced when
    /// the goods went out, and posting it again on the way back would report every contract's
    /// hire twice. Extension days are the exception: they are charged as they are consumed, so
    /// an extension signed and never used costs nothing, and the days that are used have not
    /// reached the ledger before now.
    /// </para>
    /// </summary>
    private static List<RentalCharge> StoreCharges(
        RentalContract contract, Engine.ContractSettlement settlement, Guid noteId) =>
        settlement.Charges
            .Where(c => c.ReturnNoteId == noteId)
            .Select(c => new RentalCharge
            {
                ContractId = contract.Id,
                ContractLineId = c.LineId,
                Kind = (RentalChargeKind)c.Kind,
                Amount = c.Amount,
                ReturnNoteId = c.ReturnNoteId,
                IssueNoteId = c.IssueNoteId,
                ExtensionId = c.ExtensionId,
                Qty = c.Qty,
                Days = c.Days,
                UnitPrice = c.UnitPrice,
                IsPosted = c.Kind != Engine.RentalChargeKind.Rent || c.ExtensionId is not null
            })
            .ToList();

    // ---------- extension ----------

    /// <summary>
    /// Days added to a period at the rental price.
    ///
    /// <para>
    /// It moves no stock — the goods are already on site — and it posts nothing, because
    /// extension days are charged as they are consumed rather than in advance. What it does do
    /// is record the two dates the approval can be re-checked against later: the day it was
    /// signed, and the deadline it had to beat.
    /// </para>
    /// </summary>
    public async Task<RentalResult<ContractExtension>> ExtendAsync(
        Guid contractId, ContractExtensionRequest r, bool isManager, Guid? userId, string? userName,
        CancellationToken ct = default)
    {
        var docs = await LoadAsync(contractId, ct);
        if (docs is null) return RentalResult<ContractExtension>.NotFound("Contract not found.");
        if (NotOpen(docs.Contract) is string shut) return RentalResult<ContractExtension>.Conflict(shut);

        var zone = Zone();
        var lines = r.ContractLineId is Guid lineId
            ? docs.Lines.Where(l => l.Id == lineId).ToList()
            : docs.Lines;
        if (lines.Count == 0) return RentalResult<ContractExtension>.NotFound("Contract line not found.");

        // A price is required on a contract-wide extension: one document cannot carry a
        // different rate per line, and picking one line's rate for all of them would misprice
        // the rest without anything saying so.
        var unitPrice = r.UnitPrice ?? (r.ContractLineId is null ? (decimal?)null : lines[0].RentUnitPrice);
        if (unitPrice is null)
            return RentalResult<ContractExtension>.Invalid(
                "An extension covering every line needs an explicit price.");

        var periodEnd = SoonestPeriodEnd(docs, lines, zone);
        if (periodEnd is null)
            return RentalResult<ContractExtension>.Conflict(
                "Nothing is out on this contract, so there is no period to extend.");

        var raisedAt = r.RaisedAtUtc ?? DateTime.UtcNow;
        var candidate = new Engine.RentalExtension(
            Guid.NewGuid(), r.ContractLineId, r.ExtraDays, unitPrice.Value,
            Engine.RentalPricing.LocalDay(raisedAt, zone), r.Reason);

        if (Engine.RentalRules.ExtensionProblem(candidate, periodEnd.Value, isManager) is string problem)
            return problem switch
            {
                "daysRequired" => RentalResult<ContractExtension>.Invalid("An extension adds at least one day."),
                "negativePrice" => RentalResult<ContractExtension>.Invalid("An extension price cannot be negative."),
                // Left open to everyone, every overdue becomes an extension and the overdue line
                // stops meaning anything — which is the whole reason the two are separate.
                "retroactiveNeedsManager" => RentalResult<ContractExtension>.Forbidden(
                    "This period has already ended. A retroactive extension needs a manager."),
                "retroactiveNeedsReason" => RentalResult<ContractExtension>.Invalid(
                    "A retroactive extension needs a written reason."),
                _ => RentalResult<ContractExtension>.Invalid(problem)
            };

        var extension = new ContractExtension
        {
            Id = candidate.Id,
            ContractId = contractId,
            ContractLineId = r.ContractLineId,
            ExtraDays = r.ExtraDays,
            UnitPrice = unitPrice.Value,
            RaisedOnUtc = raisedAt,
            // Both dates are stored, and the flag with them. Derived on the way in and frozen:
            // the batches it was measured against will have been returned long before anyone
            // asks whether the manager's approval was needed.
            PeriodEndsOnUtc = periodEnd.Value.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            IsRetroactive = Engine.RentalRules.IsRetroactive(candidate, periodEnd.Value),
            ApprovedByUserId = userId,
            ApprovedByName = userName,
            Reason = r.Reason
        };

        await using var tx = await db.BeginWriteScopeAsync(ct);
        db.ContractExtensions.Add(extension);
        extension.Number = Numbered("EXT", await db.ContractExtensions.CountAsync(ct) + 1);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return RentalResult<ContractExtension>.Success(extension);
    }

    /// <summary>
    /// The deadline an extension has to beat: the earliest expiring batch still out across the
    /// lines it covers, entitlement already signed included. Null when nothing is out.
    /// </summary>
    private static DateOnly? SoonestPeriodEnd(Documents docs, List<RentalContractLine> lines, TimeZoneInfo zone)
    {
        var batches = Batches(docs, zone);
        var entries = Entries(docs, zone);
        var engineLines = docs.Lines.Select(EngineLine).ToList();

        DateOnly? soonest = null;
        foreach (var line in lines)
        {
            var engineLine = EngineLine(line);
            var entitled = Engine.RentalPeriod.EntitledDays(Engine.RentalPeriod.Entitlement(
                line.RentUnitPrice,
                Engine.RentalSettlement.DurationOf(engineLine, engineLines),
                Engine.RentalSettlement.ExtensionsFor(engineLine, Extensions(docs, zone))));

            var open = OpenBatches(
                batches.Where(b => b.LineId == line.Id).ToList(),
                entries.Where(e => e.LineId == line.Id).Sum(e => e.SettledQty));

            var end = Engine.RentalRules.SoonestPeriodEnd(open, entitled);
            if (end is DateOnly value && (soonest is null || value < soonest)) soonest = value;
        }
        return soonest;
    }

    /// <summary>
    /// A note is numbered after its stock has moved (the number is counted inside the same write
    /// scope), so its movements, already added, take the number here rather than the order of the
    /// steps changing for them.
    /// </summary>
    private void StampDocumentNumber(Guid documentId, string number)
    {
        foreach (var entry in db.ChangeTracker.Entries<StockMovement>())
            if (entry.State == EntityState.Added && entry.Entity.DocumentId == documentId)
                entry.Entity.DocumentNumber = number;
    }

    /// <summary>
    /// The batches still out, oldest closing first — the same order the returns consume them
    /// in, so "what is about to expire" is answered from the same match the money uses.
    /// </summary>
    private static List<Engine.IssueBatch> OpenBatches(IReadOnlyList<Engine.IssueBatch> batches, decimal settledQty)
    {
        var open = new List<Engine.IssueBatch>();
        var consumed = Math.Max(0m, settledQty);
        foreach (var batch in Engine.RentalFifo.Oldest(batches))
        {
            if (consumed >= batch.Qty) { consumed -= batch.Qty; continue; }
            open.Add(consumed > 0 ? batch with { Qty = batch.Qty - consumed } : batch);
            consumed = 0;
        }
        return open;
    }

    private static List<OutstandingBatch> Outstanding(Documents docs, TimeZoneInfo zone)
    {
        var batches = Batches(docs, zone);
        var entries = Entries(docs, zone);
        var engineLines = docs.Lines.Select(EngineLine).ToList();
        var extensions = Extensions(docs, zone);
        var issuedFrom = docs.IssueNotes.ToDictionary(n => n.Id, n => n.BranchId);

        var list = new List<OutstandingBatch>();
        foreach (var line in docs.Lines)
        {
            var engineLine = EngineLine(line);
            var entitled = Engine.RentalPeriod.EntitledDays(Engine.RentalPeriod.Entitlement(
                line.RentUnitPrice,
                Engine.RentalSettlement.DurationOf(engineLine, engineLines),
                Engine.RentalSettlement.ExtensionsFor(engineLine, extensions)));
            var open = OpenBatches(
                batches.Where(b => b.LineId == line.Id).ToList(),
                entries.Where(e => e.LineId == line.Id).Sum(e => e.SettledQty));
            foreach (var batch in open)
                list.Add(new OutstandingBatch(
                    docs.Contract.Id, line, batch.IssueNoteId,
                    issuedFrom.GetValueOrDefault(batch.IssueNoteId, docs.Contract.BranchId),
                    batch.IssuedOn, Engine.RentalPricing.PeriodEndsOn(batch.IssuedOn, entitled), batch.Qty));
        }
        return list;
    }

    // ---------- reading ----------

    /// <summary>Today in the company's zone — what 'overdue' is measured against.</summary>
    public DateOnly Today() => Engine.RentalPricing.LocalDay(DateTime.UtcNow, Zone());

    /// <summary>
    /// What is still out across every contract, batch by batch — the same match the money and the extension use —
    /// optionally for one article. Set queries, never one load per contract: ...
    /// </summary>
    public async Task<IReadOnlyList<OutstandingBatch>> OutstandingAsync(Guid? productId, CancellationToken ct = default)
    {
        var issueQuery = db.IssueNoteLines.AsNoTracking();
        var returnQuery = db.ReturnNoteLines.AsNoTracking();
        if (productId.HasValue)
        {
            issueQuery = issueQuery.Where(l => l.ContractLine!.ProductId == productId.Value);
            returnQuery = returnQuery.Where(l => l.ContractLine!.ProductId == productId.Value);
        }

        var issueRows = await issueQuery
            .Select(l => new { l.ContractLineId, ContractId = l.IssueNote!.ContractId, l.Qty })
            .ToListAsync(ct);
        var returnRows = await returnQuery
            .Select(l => new { l.ContractLineId, Settled = l.QtyOk + l.QtyPartialDamage + l.QtyTotalDamage + l.QtyMissing })
            .ToListAsync(ct);

        var settledByLine = returnRows.GroupBy(r => r.ContractLineId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Settled));

        var openContractIds = issueRows.GroupBy(r => (r.ContractLineId, r.ContractId))
            .Where(g => g.Sum(x => x.Qty) > settledByLine.GetValueOrDefault(g.Key.ContractLineId, 0m))
            .Select(g => g.Key.ContractId)
            .Distinct()
            .ToList();

        if (openContractIds.Count == 0) return Array.Empty<OutstandingBatch>();

        var cutoff = await db.Stores.AsNoTracking().Where(s => !s.IsDeleted)
            .Select(s => s.RentalDayCutoffHour).FirstOrDefaultAsync(ct);

        var result = new List<OutstandingBatch>();
        var zone = Zone();

        foreach (var chunk in openContractIds.Chunk(500))
        {
            var chunkIds = chunk.ToList();
            var contracts = await db.RentalContracts.AsNoTracking()
                .Where(c => chunkIds.Contains(c.Id))
                .ToListAsync(ct);
            var lines = await db.RentalContractLines.AsNoTracking()
                .Where(l => chunkIds.Contains(l.ContractId))
                .OrderBy(l => l.SortOrder)
                .ToListAsync(ct);
            var issueNotes = await db.IssueNotes.AsNoTracking().Include(n => n.Lines)
                .Where(n => chunkIds.Contains(n.ContractId))
                .OrderBy(n => n.IssuedAtUtc).ThenBy(n => n.CreatedAtUtc)
                .ToListAsync(ct);
            var returnNotes = await db.ReturnNotes.AsNoTracking().Include(n => n.Lines)
                .Where(n => chunkIds.Contains(n.ContractId))
                .OrderBy(n => n.ReturnedAtUtc).ThenBy(n => n.CreatedAtUtc)
                .ToListAsync(ct);
            var extensions = await db.ContractExtensions.AsNoTracking()
                .Where(e => chunkIds.Contains(e.ContractId))
                .OrderBy(e => e.RaisedOnUtc).ThenBy(e => e.CreatedAtUtc)
                .ToListAsync(ct);

            var linesByContract = lines.GroupBy(l => l.ContractId).ToDictionary(g => g.Key, g => g.ToList());
            var issueByContract = issueNotes.GroupBy(n => n.ContractId).ToDictionary(g => g.Key, g => g.ToList());
            var returnByContract = returnNotes.GroupBy(n => n.ContractId).ToDictionary(g => g.Key, g => g.ToList());
            var extByContract = extensions.GroupBy(e => e.ContractId).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var contract in contracts)
            {
                var docs = new Documents(
                    contract,
                    linesByContract.GetValueOrDefault(contract.Id) ?? [],
                    issueByContract.GetValueOrDefault(contract.Id) ?? [],
                    returnByContract.GetValueOrDefault(contract.Id) ?? [],
                    extByContract.GetValueOrDefault(contract.Id) ?? [],
                    cutoff);

                var batches = Outstanding(docs, zone);
                foreach (var b in batches)
                {
                    if (b.Qty > 0 && (!productId.HasValue || b.Line.ProductId == productId.Value))
                    {
                        result.Add(b);
                    }
                }
            }
        }

        return result;
    }

    public async Task<Engine.ContractSettlement?> SettleAsync(Guid contractId, CancellationToken ct = default)
    {
        var docs = await LoadAsync(contractId, ct, tracking: false);
        return docs is null ? null : Engine.RentalSettlement.Settle(Input(docs, Zone()), regionalSettings.Get());
    }

    public async Task<ReturnPreviewDto?> PreviewReturnAsync(Guid contractId, ReturnNoteRequest request, CancellationToken ct = default)
    {
        var docs = await LoadAsync(contractId, ct, tracking: false);
        if (docs is null) return null;
        var zone = Zone();
        var regional = regionalSettings.Get();
        var returnedAt = request.ReturnedAtUtc ?? DateTime.UtcNow;
        var returnedDay = Engine.RentalPricing.LocalDay(returnedAt, zone);
        var noteId = Guid.NewGuid();
        var prior = Entries(docs, zone);
        var hypothetical = new List<Engine.ReturnEntry>();
        var draftLines = new List<(ReturnNoteLine Line, int Index)>();
        var problems = new List<string>();
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var row = request.Lines[i];
            if (!docs.Lines.Any(l => l.Id == row.ContractLineId)) { problems.Add("returnLineInvalid"); continue; }
            var usage = SameDayUsage.LegacyUnknown;
            if (!string.IsNullOrWhiteSpace(row.SameDayUsage)
                && (!Enum.TryParse(row.SameDayUsage, true, out usage)
                    || usage is not (SameDayUsage.Used or SameDayUsage.NotUsed)))
            { problems.Add("sameDayUsageInvalid"); continue; }
            if (row.QtyOk + row.QtyPartialDamage + row.QtyTotalDamage == 0) usage = SameDayUsage.NotApplicable;
            var line = new ReturnNoteLine
            {
                Id = Guid.NewGuid(), ReturnNoteId = noteId, ContractLineId = row.ContractLineId,
                QtyOk = row.QtyOk, QtyPartialDamage = row.QtyPartialDamage, MaintenanceValue = row.MaintenanceValue,
                QtyTotalDamage = row.QtyTotalDamage, QtyMissing = row.QtyMissing, SameDayUsage = usage,
                ReportedOnUtc = row.ReportedOnUtc
            };
            draftLines.Add((line, i));
            hypothetical.Add(new Engine.ReturnEntry(noteId, row.ContractLineId, returnedDay,
                row.QtyOk, row.QtyPartialDamage, row.MaintenanceValue, row.QtyTotalDamage, row.QtyMissing,
                row.ReportedOnUtc is DateTime reported ? Engine.RentalPricing.LocalDay(reported, zone) : null,
                request.CountReturnDay ?? Engine.RentalDays.CountsReturnDayByDefault(Engine.RentalDays.LocalTime(returnedAt, zone), docs.DayCutoffHour),
                request.ChargeOverdue == false, line.Id, usage));
        }
        if (request.Lines.Count == 0) problems.Add("returnLinesRequired");

        var batches = Batches(docs, zone);
        foreach (var entry in hypothetical)
        {
            var issued = batches.Where(b => b.LineId == entry.LineId).Sum(b => b.Qty);
            var settled = prior.Concat(hypothetical.TakeWhile(e => e.ReturnLineId != entry.ReturnLineId))
                .Where(e => e.LineId == entry.LineId).Sum(e => e.SettledQty);
            if (Engine.RentalRules.ReturnProblem(entry, issued, settled) is string problem) problems.Add(problem);
        }
        // The note as it would stand with no answer: its late days and late total keep the
        // question on screen after any answer, and are what a deduction is measured against.
        var countReturnDay = request.CountReturnDay
            ?? Engine.RentalDays.CountsReturnDayByDefault(Engine.RentalDays.LocalTime(returnedAt, zone), docs.DayCutoffHour);
        var unanswered = docs with { ReturnNotes = docs.ReturnNotes.Append(new ReturnNote
        {
            Id = noteId, ContractId = contractId, ReturnedAtUtc = returnedAt, CountReturnDay = countReturnDay,
            Lines = draftLines.Select(x => x.Line).ToList()
        }).ToList() };
        var lateCharges = Engine.RentalSettlement.Settle(Input(unanswered, zone), regional).Charges
            .Where(c => c.ReturnNoteId == noteId && c.Kind == Engine.RentalChargeKind.Overdue).ToList();
        int? daysWaived = Deducts(request) ? request.LateDaysWaived : null;
        decimal? amountWaived = Deducts(request) ? request.LateAmountWaived : null;
        decimal? amountFull = amountWaived is null ? null : Engine.RentalLateReturn.LateAmount(lateCharges);
        hypothetical = hypothetical.Select(e => e with
            { LateDaysWaived = daysWaived, LateAmountWaived = amountWaived, LateAmountFull = amountFull }).ToList();

        var allEntries = prior.Concat(hypothetical).ToList();
        var allocations = new List<(ReturnNoteLine Line, int Index, Engine.RentalReturnAllocation Allocation, RentalContractLine ContractLine)>();
        foreach (var (line, index) in draftLines)
        {
            var contractLine = docs.Lines.Single(l => l.Id == line.ContractLineId);
            var engineLine = EngineLine(contractLine);
            var portions = Engine.RentalSettlement.AllocateLine(engineLine,
                Engine.RentalSettlement.DurationOf(engineLine, docs.Lines.Select(EngineLine).ToList()),
                batches.Where(b => b.LineId == line.ContractLineId).ToList(),
                allEntries.Where(e => e.LineId == line.ContractLineId).ToList(),
                Engine.RentalSettlement.ExtensionsFor(engineLine, Extensions(docs, zone)), regional,
                docs.Contract.MinimumRentalDays, allowMissingUsage: true)
                .Where(a => a.Return.ReturnLineId == line.Id).ToList();
            var need = portions.Any(a => a.Condition != RentalReturnCondition.Missing && a.Batch.ActualIssuedOn == returnedDay);
            if (need && line.SameDayUsage is not (SameDayUsage.Used or SameDayUsage.NotUsed))
                problems.Add("sameDayUsageRequired");
            allocations.AddRange(portions.Select(a => (line, index, a, contractLine)));
        }

        var before = Engine.RentalSettlement.Settle(Input(docs, zone), regional);
        var simulated = docs with { ReturnNotes = docs.ReturnNotes.Append(new ReturnNote
        {
            Id = noteId, ContractId = contractId, ReturnedAtUtc = returnedAt,
            CountReturnDay = countReturnDay,
            WaiveOverdue = request.ChargeOverdue == false,
            LateDaysWaived = daysWaived, LateAmountWaived = amountWaived, LateAmountFull = amountFull,
            Lines = draftLines.Select(x => x.Line).ToList()
        }).ToList() };
        var after = Engine.RentalSettlement.Settle(Input(simulated, zone), regional);
        var newCharges = after.Charges.Where(c => c.ReturnNoteId == noteId).ToList();
        var byRate = new Dictionary<decimal, decimal>();
        foreach (var charge in newCharges.Where(c => c.Kind != Engine.RentalChargeKind.Rent || c.ExtensionId is not null))
            byRate[docs.Lines.FirstOrDefault(l => l.Id == charge.LineId)?.TaxRate ?? 0m] =
                byRate.GetValueOrDefault(docs.Lines.FirstOrDefault(l => l.Id == charge.LineId)?.TaxRate ?? 0m) + charge.Amount;
        foreach (var group in allocations.Where(x => x.Allocation.UnusedRent > 0).GroupBy(x => x.ContractLine.TaxRate))
            byRate[group.Key] = byRate.GetValueOrDefault(group.Key) - group.Sum(x => x.Allocation.UnusedRent);
        var tax = TaxOn(regional, byRate);
        var unused = allocations.Sum(x => x.Allocation.UnusedRent);
        var unusedTax = TaxOn(regional, allocations.Where(x => x.Allocation.UnusedRent > 0)
            .GroupBy(x => x.ContractLine.TaxRate)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Allocation.UnusedRent)));
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == docs.Contract.CustomerId, ct);
        var owed = newCharges.Where(c => (c.Kind != Engine.RentalChargeKind.Rent || c.ExtensionId is not null)).Sum(c => c.Amount);
        var relief = Engine.RentalSettlement.Relieve(unused + unusedTax,
            (customer?.Balance ?? 0m) + owed + tax + unusedTax);
        // The issue invoice already raised the full advance for the entire contract. This
        // simulated note contributes only its posted charges, its unused-rent credit, and the
        // tax delta; the existing customer balance already contains the invoice and receipts.
        var balanceAfter = (customer?.Balance ?? 0m) + owed - unused + tax;
        var issueIds = batches.Select(b => b.IssueNoteId).Distinct().ToList();
        var invoiceIds = await db.Invoices.AsNoTracking().Where(i => i.SourceType == LedgerSources.RentalIssue
                && i.SourceId != null && issueIds.Contains(i.SourceId.Value)).Select(i => i.Id).ToListAsync(ct);
        var allocatedReceipts = await db.ReceiptAllocations.AsNoTracking()
            .Join(db.ReceiptVouchers.AsNoTracking().Where(v => v.Status == PaymentStatus.Confirmed),
                a => a.ReceiptVoucherId, v => v.Id, (a, v) => new { a.InvoiceId, a.ReceiptVoucherId, a.Amount, v.ContractId })
            .Where(a => invoiceIds.Contains(a.InvoiceId)).ToListAsync(ct);
        var contractReceipts = await db.ReceiptVouchers.AsNoTracking()
            .Where(v => v.ContractId == contractId && v.Status == PaymentStatus.Confirmed)
            .Select(v => new { v.Id, v.Amount }).ToListAsync(ct);
        var contractReceiptIds = contractReceipts.Select(v => v.Id).ToHashSet();
        var previouslyPaid = contractReceipts.Sum(v => v.Amount)
            + allocatedReceipts.Where(a => a.ContractId != contractId && !contractReceiptIds.Contains(a.ReceiptVoucherId))
                .Sum(a => a.Amount);
        var rows = allocations.Select(x =>
        {
            var sameDay = x.Allocation.Condition != RentalReturnCondition.Missing
                && x.Allocation.Batch.ActualIssuedOn == returnedDay;
            var missingChoice = sameDay && x.Line.SameDayUsage is not (SameDayUsage.Used or SameDayUsage.NotUsed);
            var amount = x.Allocation.ActualRent;
            var issue = docs.IssueNotes.First(n => n.Id == x.Allocation.Batch.IssueNoteId);
            return new ReturnPreviewRowDto(x.Index, x.ContractLine.Id, issue.Id, issue.Number, issue.IssuedAtUtc,
                x.Allocation.Qty, x.Allocation.Condition == RentalReturnCondition.Missing ? 0 : x.Allocation.Qty,
                sameDay, sameDay && !missingChoice ? x.Line.SameDayUsage.ToString() : null,
                missingChoice ? null : x.Allocation.Stay.ChargedDays, x.ContractLine.RentUnitPrice,
                missingChoice ? null : amount);
        }).ToList();
        // Keep the late-return question visible, with the figures before any answer, after it is answered.
        ReturnLatePreviewDto? late = null;
        if (lateCharges.Count > 0)
        {
            var due = allocations.Where(x => lateCharges.Any(c => c.LineId == x.ContractLine.Id
                    && c.IssueNoteId == x.Allocation.Batch.IssueNoteId))
                .Select(x => {
                    var engineLine = EngineLine(x.ContractLine);
                    var duration = Engine.RentalSettlement.DurationOf(engineLine, docs.Lines.Select(EngineLine).ToList());
                    var entitlement = Engine.RentalPeriod.Entitlement(engineLine.RentUnitPrice, duration,
                        Engine.RentalSettlement.ExtensionsFor(engineLine, Extensions(docs, zone)));
                    return x.Allocation.Batch.IssuedOn.AddDays(Engine.RentalPeriod.EntitledDays(entitlement));
                }).Min();
            var deadline = due.ToDateTime(new TimeOnly(docs.DayCutoffHour ?? 0, 0));
            late = new ReturnLatePreviewDto(due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), lateCharges.Max(c => c.Days),
                lateCharges.Sum(c => c.Amount), (int)Math.Max(0, (Engine.RentalDays.LocalTime(returnedAt, zone) - deadline).TotalMinutes));
            if (Engine.RentalLateReturn.DeductionProblem(request.ChargeOverdue, request.LateDaysWaived,
                    request.LateAmountWaived, late.Days, late.Amount, regional) is string deductionProblem)
                problems.Add(deductionProblem);
            if (Engine.RentalLateReturn.WaiverProblem(request.ChargeOverdue, request.LateWaiverReason,
                    Deducts(request)) is string waiverProblem)
                problems.Add(waiverProblem);
        }
        var canConfirm = problems.Count == 0;
        var mapped = newCharges.Select(c => new RentalChargeDto(Guid.Empty, c.LineId, c.Kind.ToString(), c.Amount,
            noteId, c.IssueNoteId, c.ExtensionId, c.Qty, c.Days, c.UnitPrice, c.Kind != Engine.RentalChargeKind.Rent || c.ExtensionId is not null)).ToList();
        return new ReturnPreviewDto(rows, mapped, canConfirm ? newCharges.Sum(c => c.Amount) : null,
            canConfirm ? unused : null, customer?.Balance ?? 0m, previouslyPaid,
            canConfirm ? Math.Max(0m, -balanceAfter) : null,
            canConfirm ? relief.DebtReduction : null, canConfirm ? relief.PaymentVoucher : null,
            after.IsClosed, late, canConfirm, problems.Distinct().ToList());
    }

    /// <summary>Whether the answer is yes less a deduction — the only yes that a note keeps a deduction with.</summary>
    private static bool Deducts(ReturnNoteRequest request) =>
        request.ChargeOverdue == true && (request.LateDaysWaived is not null || request.LateAmountWaived is not null);

    public Task<Documents?> ReadAsync(Guid contractId, CancellationToken ct = default) =>
        LoadAsync(contractId, ct, tracking: false);

    internal Task<Documents?> ReadForCorrectionAsync(Guid contractId, CancellationToken ct) =>
        LoadAsync(contractId, ct);

    /// <summary>
    /// The list's figures for many contracts at once: what went out, what came back or was
    /// written off, what is still on site, and what was charged (docs/07 §3.1).
    ///
    /// <para>
    /// Three set queries on the id list — issue lines, return lines, charges — and never one
    /// per contract: the list used to settle each open contract to answer its own question,
    /// which was a five-query load plus a full engine run sixty times over. Everything the row
    /// needs is already persisted, so it is read, not recomputed. The sums are taken in memory
    /// after <c>ToListAsync</c> because SQLite stores decimal as TEXT and the provider cannot
    /// translate <c>Sum</c> over it.
    /// </para>
    /// <para>
    /// The figures are <b>what was charged</b>, while the contract screen recomputes from the
    /// documents. They agree today by construction (<c>StoreCharges</c> writes every engine
    /// charge); a future engine correction would show on the contract before it showed here.
    /// <c>StillOutQty</c> is issued − settled, floored at zero, which is the engine's figure
    /// because <see cref="ReturnAsync"/> refuses over-returns. A contract with no documents maps
    /// to <see cref="ContractTotals.Zero"/>, so every id asked for is answered.
    /// </para>
    /// <para>
    /// The <c>IN</c> list is the route's 500-row cap at most — inside MySQL 8's limits and
    /// inside the bundled SQLite's (32,766 variables in the native build EF ships; 999 only on
    /// builds older than 3.32). A <c>Take</c> above that would need chunking here.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, ContractTotals>> TotalsAsync(
        IReadOnlyCollection<Guid> contractIds, CancellationToken ct = default)
    {
        var ids = contractIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, ContractTotals>();

        var issued = await db.IssueNoteLines.AsNoTracking()
            .Where(l => ids.Contains(l.IssueNote!.ContractId))
            .Select(l => new { l.IssueNote!.ContractId, l.Qty })
            .ToListAsync(ct);
        var returned = await db.ReturnNoteLines.AsNoTracking()
            .Where(l => ids.Contains(l.ReturnNote!.ContractId))
            .Select(l => new { l.ReturnNote!.ContractId, Settled = l.QtyOk + l.QtyPartialDamage + l.QtyTotalDamage + l.QtyMissing })
            .ToListAsync(ct);
        var charged = await db.RentalCharges.AsNoTracking()
            .Where(c => ids.Contains(c.ContractId))
            .Select(c => new { c.ContractId, c.Kind, c.Amount })
            .ToListAsync(ct);

        var issuedBy = issued.GroupBy(x => x.ContractId).ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));
        var settledBy = returned.GroupBy(x => x.ContractId).ToDictionary(g => g.Key, g => g.Sum(x => x.Settled));
        var chargedBy = charged.GroupBy(x => x.ContractId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
        var overdueBy = charged.Where(x => x.Kind == RentalChargeKind.Overdue)
            .GroupBy(x => x.ContractId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

        return ids.ToDictionary(id => id, id =>
        {
            var issuedQty = issuedBy.GetValueOrDefault(id);
            var settledQty = settledBy.GetValueOrDefault(id);
            return new ContractTotals(
                issuedQty, settledQty, Math.Max(0m, issuedQty - settledQty),
                chargedBy.GetValueOrDefault(id), overdueBy.GetValueOrDefault(id));
        });
    }

    /// <summary>
    /// The authoritative full-period rental amount for one contracted line, before tax.
    /// </summary>
    public decimal ContractLineRent(Documents docs, RentalContractLine line)
    {
        var lines = docs.Lines.Select(EngineLine).ToList();
        return Engine.RentalPricing.Amount(regionalSettings.Get(), line.RentUnitPrice,
            Engine.RentalSettlement.DurationOf(EngineLine(line), lines), line.Qty);
    }

    /// <summary>Full agreed period on the quantity this issue note put out, at the signed daily rates.</summary>
    public decimal PeriodRent(Documents docs, IssueNote note)
    {
        var regional = regionalSettings.Get();
        var engineLines = docs.Lines.Select(EngineLine).ToList();

        var total = 0m;
        foreach (var line in note.Lines)
        {
            var contractLine = docs.Lines.FirstOrDefault(l => l.Id == line.ContractLineId);
            if (contractLine is null) continue;
            // An accessory rents at zero and adds nothing, which is what "no value" means: it
            // applies to the rent line only, never to the compensation if it goes missing.
            total += Engine.RentalPricing.Amount(
                regional, contractLine.RentUnitPrice,
                Engine.RentalSettlement.DurationOf(EngineLine(contractLine), engineLines), line.Qty);
        }
        return total;
    }

    /// <summary>
    /// One rental line as an invoice has to present it.
    ///
    /// <para>
    /// A hire billed at issue carries its <see cref="Days"/>: quantity × daily price × days is the
    /// line, and the paper prints the three in their own columns. A return-note charge — overdue
    /// across spans at different prices, a repair, a loss — has no single daily price, so it keeps
    /// quantity 1 and the detail in the name: a printed invoice that does not add up is worse than
    /// one with less detail on it.
    /// </para>
    /// </summary>
    public sealed record BillingLine(
        Guid? ProductId, string Sku, string Name, decimal Qty, decimal UnitPrice,
        decimal TaxRate, decimal Net, string IncomeAccountCode, int? Days = null,
        Guid? ContractLineId = null, Guid? ReturnAllocationId = null);

    /// <summary>
    /// What one issue note put on the customer's account, line by line, so an invoice can bill
    /// it without re-deriving a single figure of the arithmetic. Null when there is no such note.
    /// </summary>
    public async Task<IReadOnlyList<BillingLine>?> IssueNoteBillingAsync(Guid noteId, CancellationToken ct = default)
    {
        var note = await db.IssueNotes.AsNoTracking().Include(n => n.Lines)
            .FirstOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null) return null;

        var docs = await LoadAsync(note.ContractId, ct, tracking: false);
        if (docs is null) return null;

        var regional = regionalSettings.Get();
        var engineLines = docs.Lines.Select(EngineLine).ToList();
        var allocations = await db.ReturnNoteLineAllocations.AsNoTracking()
            .Where(a => a.IssueNoteId == noteId).OrderBy(a => a.ReturnedAtUtc).ToListAsync(ct);

        var billed = new List<BillingLine>();
        foreach (var line in note.Lines)
        {
            var contractLine = docs.Lines.FirstOrDefault(l => l.Id == line.ContractLineId);
            if (contractLine is null) continue;

            var days = Engine.RentalSettlement.DurationOf(EngineLine(contractLine), engineLines);
            var returned = allocations.Where(a => a.ContractLineId == line.ContractLineId).ToList();
            var settledQty = returned.Sum(a => a.Qty);
            var stillOut = Math.Max(0m, line.Qty - settledQty);
            if (stillOut > 0)
                AddBilling(stillOut, days, null);
            foreach (var allocation in returned)
                AddBilling(allocation.Qty, Math.Min(days, Math.Max(0, allocation.ChargeableDays)), allocation.Id);

            void AddBilling(decimal qty, int billedDays, Guid? returnAllocationId)
            {
                var net = Engine.RentalPricing.Amount(regional, contractLine.RentUnitPrice, billedDays, qty);
                // A zero-day unused return has a physical transaction and audit allocation, but
                // no monetary invoice line.
                if (net == 0) return;
                billed.Add(new BillingLine(contractLine.ProductId, contractLine.Sku, contractLine.NameSnapshot,
                    qty, contractLine.RentUnitPrice, contractLine.TaxRate, net, LedgerAccounts.RentalIncome,
                    billedDays, contractLine.Id, returnAllocationId));
            }
        }
        return billed;
    }

    /// <summary>
    /// What one return note charged: overdue days, repairs recharged and compensation for what
    /// is not coming back — and nothing else.
    ///
    /// <para>
    /// The rent recorded on the note is left out on purpose: it was invoiced when the goods went
    /// out, which is the single fact the rental module hangs off, and billing it again here
    /// would be that same double charge arriving by a new route. The charges marked as posted
    /// are exactly the ones the return put on the customer's account.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<BillingLine>?> ReturnNoteBillingAsync(Guid noteId, CancellationToken ct = default)
    {
        var note = await db.ReturnNotes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null) return null;

        var charges = await db.RentalCharges.AsNoTracking()
            .Where(c => c.ReturnNoteId == noteId).ToListAsync(ct);
        var lines = await db.RentalContractLines.AsNoTracking()
            .Where(l => l.ContractId == note.ContractId).ToDictionaryAsync(l => l.Id, ct);

        return charges
            .Where(c => c.IsPosted && c.Amount != 0)
            .Select(c =>
            {
                var line = lines.GetValueOrDefault(c.ContractLineId);
                var detail = c.Days > 0 ? $"{c.Qty:0.##} × {c.Days} j" : $"{c.Qty:0.##}";
                return new BillingLine(
                    line?.ProductId, line?.Sku ?? "", $"{line?.NameSnapshot} — {c.Kind} ({detail})",
                    1m, c.Amount, line?.TaxRate ?? 0m, c.Amount, RentalPosting.AccountFor(c.Kind));
            })
            .ToList();
    }

    /// <summary>Contracted, issued, settled and still out for every line — the numbers §5.5 asks each line to carry.</summary>
    public IReadOnlyDictionary<Guid, Engine.LineSettlement> Progress(Documents docs) =>
        Engine.RentalSettlement.Settle(Input(docs, Zone()), regionalSettings.Get())
            .Lines.ToDictionary(l => l.LineId);

    // ---------- plumbing ----------

    /// <summary>
    /// The documents of one contract, loaded in the order the engine consumes them: issue notes
    /// oldest first, return notes in the order they were taken. The sorts inside the engine are
    /// stable, so this order is what decides a tie between two notes of the same day.
    /// </summary>
    /// <param name="DayCutoffHour">
    /// The company's day cutoff at the time of reading (<see cref="Engine.RentalDays"/>): it decides
    /// the day each issue batch starts on. Read with the documents so every figure worked out
    /// from one load counts days the same way.
    /// </param>
    public sealed record Documents(
        RentalContract Contract,
        List<RentalContractLine> Lines,
        List<IssueNote> IssueNotes,
        List<ReturnNote> ReturnNotes,
        List<ContractExtension> Extensions,
        int? DayCutoffHour = null);

    private async Task<Documents?> LoadAsync(Guid contractId, CancellationToken ct, bool tracking = true)
    {
        var contract = await Query(db.RentalContracts, tracking).FirstOrDefaultAsync(c => c.Id == contractId, ct);
        if (contract is null) return null;

        var cutoff = await db.Stores.AsNoTracking().Where(s => !s.IsDeleted)
            .Select(s => s.RentalDayCutoffHour).FirstOrDefaultAsync(ct);

        return new Documents(
            contract,
            await Query(db.RentalContractLines, tracking)
                .Where(l => l.ContractId == contractId).OrderBy(l => l.SortOrder).ToListAsync(ct),
            await Query(db.IssueNotes, tracking).Include(n => n.Lines)
                .Where(n => n.ContractId == contractId)
                .OrderBy(n => n.IssuedAtUtc).ThenBy(n => n.CreatedAtUtc).ToListAsync(ct),
            await Query(db.ReturnNotes, tracking).Include(n => n.Lines).ThenInclude(l => l.Allocations)
                .Where(n => n.ContractId == contractId)
                .OrderBy(n => n.ReturnedAtUtc).ThenBy(n => n.CreatedAtUtc).ToListAsync(ct),
            await Query(db.ContractExtensions, tracking)
                .Where(e => e.ContractId == contractId)
                .OrderBy(e => e.RaisedOnUtc).ThenBy(e => e.CreatedAtUtc).ToListAsync(ct),
            cutoff);
    }

    private static IQueryable<T> Query<T>(DbSet<T> set, bool tracking) where T : class =>
        tracking ? set : set.AsNoTracking();

    private async Task<List<Engine.IssueBatch>> BatchesAsync(Guid contractId, CancellationToken ct)
    {
        var docs = await LoadAsync(contractId, ct, tracking: false);
        return docs is null ? [] : Batches(docs, Zone());
    }

    /// <summary>
    /// The company's own time zone, which is what the day counting is done in.
    ///
    /// <para>
    /// ⚠ Never <c>.Date</c>. Notes are stored in UTC and the whole calculation counts days: a
    /// return taken at 00:30 in Algiers is stored on the previous UTC day, and counting from
    /// the UTC date would charge the customer a day they did not have — every time, on the
    /// early shift.
    /// </para>
    /// <para>A profile naming a zone this machine does not know falls back to UTC rather than
    /// throwing, exactly as the receipt clock does.</para>
    /// </summary>
    private TimeZoneInfo Zone()
    {
        var id = regionalSettings.Get().TimeZone;
        var presetWindowsId = id switch
        {
            "Africa/Algiers" or "Africa/Tunis" => "W. Central Africa Standard Time",
            "Africa/Casablanca" => "Morocco Standard Time",
            "Europe/Paris" => "Romance Standard Time",
            "Asia/Riyadh" => "Arab Standard Time",
            _ => null
        };
        // On Windows, FindSystemTimeZoneById may accept an IANA ID but apply its broad
        // Windows mapping (Algeria can inherit European daylight saving rules). Prefer the
        // explicit rule-equivalent zone for our presets before attempting that implicit map.
        if (OperatingSystem.IsWindows() && presetWindowsId is not null)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(presetWindowsId); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException)
        {
            // Company settings are stored as IANA names, while Windows hosts may require a
            // Windows zone ID. Keep same-calendar-day pricing on the configured company clock.
            if (presetWindowsId is not null)
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(presetWindowsId); }
                catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
            }
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId))
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(windowsId); }
                catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
            }
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId))
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(ianaId); }
                catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
            }
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    internal static Engine.RentalLine EngineLine(RentalContractLine line) =>
        new(line.Id, line.Qty, line.DurationDays, line.RentUnitPrice, line.DamageUnitPrice,
            line.OverdueUnitPrice, line.ParentLineId);

    /// <summary>
    /// Each batch starts on the day its period starts: the delivery day, or the next one for goods
    /// that left at or after the company's cutoff hour (answer 5).
    /// </summary>
    private static List<Engine.IssueBatch> Batches(Documents docs, TimeZoneInfo zone) =>
        docs.IssueNotes.SelectMany(n => n.Lines.Select(l => new Engine.IssueBatch(
            n.Id, l.ContractLineId, StartsOn(n, zone, docs.DayCutoffHour), l.Qty,
            Engine.RentalPricing.LocalDay(n.IssuedAtUtc, zone)))).ToList();

    private static DateOnly StartsOn(IssueNote note, TimeZoneInfo zone, int? cutoffHour) =>
        note.PeriodStartsOn
        ?? Engine.RentalDays.PeriodStartsOn(Engine.RentalDays.LocalTime(note.IssuedAtUtc, zone), cutoffHour);

    /// <summary>The first day of one issue note's period, as the arithmetic counts it — for the screens and the print.</summary>
    public DateOnly PeriodStartsOn(Documents docs, IssueNote note) => StartsOn(note, Zone(), docs.DayCutoffHour);

    /// <summary>When one issue note's rent enters the books: the start of its period's first day (REN-02).</summary>
    public DateTime PeriodStartsUtc(Documents docs, IssueNote note) => StartOfDayUtc(PeriodStartsOn(docs, note), Zone());

    /// <summary>
    /// Midnight of a day on the company's clock, as the instant the ledger stores — the way a
    /// chosen day is dated everywhere else (customer and supplier opening balances).
    /// </summary>
    private static DateTime StartOfDayUtc(DateOnly day, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified), zone);

    /// <summary>One contract's documents as the engine prices them — the same input the settlement uses (the quantity statement reads it).</summary>
    public Engine.RentalContractInput EngineInput(Documents docs) => Input(docs, Zone());

    /// <summary>The company's time zone, as the day counting reads it.</summary>
    public TimeZoneInfo CompanyZone() => Zone();

    /// <summary>
    /// Each note is counted with the decisions stored on it — the return day, and the late days
    /// waived or not — never recomputed from today's hour.
    /// </summary>
    private static List<Engine.ReturnEntry> Entries(Documents docs, TimeZoneInfo zone) =>
        docs.ReturnNotes.SelectMany(n => n.Lines.Select(l => new Engine.ReturnEntry(
            n.Id, l.ContractLineId, Engine.RentalPricing.LocalDay(n.ReturnedAtUtc, zone),
            l.QtyOk, l.QtyPartialDamage, l.MaintenanceValue, l.QtyTotalDamage, l.QtyMissing,
            l.ReportedOnUtc is DateTime reported ? Engine.RentalPricing.LocalDay(reported, zone) : null,
            n.CountReturnDay, n.WaiveOverdue, l.Id, l.SameDayUsage,
            n.LateDaysWaived, n.LateAmountWaived, n.LateAmountFull))).ToList();

    private static Engine.SignatoryIdentity IdentityOf(RentalContract contract) => new(
        contract.SignatoryIdType, contract.SignatoryIdNumber, contract.SignatoryIdIssuer,
        contract.SignatoryIdIssuedOn, contract.SignatoryBirthDate);

    private static string IdentityWord(string code) => code switch
    {
        "idType" => "the identity document type",
        "idNumber" => "its number",
        "idIssuer" => "who issued it",
        "idIssuedOn" => "its issue date",
        "birthDate" => "the date of birth",
        _ => code
    };

    private static string TransportWord(string code) => code switch
    {
        "driverName" => "the driver's name",
        "driverPhone" => "the driver's phone",
        "truckNumber" => "the truck number",
        _ => code
    };

    /// <summary>"a", "a and b", "a, b and c".</summary>
    private static string Words(IReadOnlyList<string> codes, Func<string, string> word)
    {
        var words = codes.Select(word).ToList();
        return words.Count <= 1 ? string.Concat(words) : string.Join(", ", words.Take(words.Count - 1)) + " and " + words[^1];
    }

    private static string? TransportProblem(string document, string? driverName, string? driverPhone, string? truckNumber)
    {
        var missing = Engine.RentalRules.TransportMissing(driverName, driverPhone, truckNumber);
        if (missing.Count == 0) return null;
        return $"{document} needs the driver's name, the driver's phone and the truck number: " +
               $"{Words(missing, TransportWord)} {(missing.Count == 1 ? "is" : "are")} missing. " +
               "When the customer carries the goods himself, mark the note as transported by the customer instead.";
    }

    // ---------- extension as a new contract (answer 8) ----------

    /// <summary>
    /// The owner's extension: a new contract for the customer, marked as the extension of this one.
    ///
    /// <para>
    /// Every batch still out is <b>carried over</b>: this contract takes it back on a carry-over
    /// return dated the day its period ends, and the new contract takes it out on a carry-over
    /// issue whose period starts that same day — "if the contract has ended and is extended, the
    /// extension counts from the end of the previous one". No stock moves and no driver is asked:
    /// the goods never left the site. The new period is billed as any issue bills, and this
    /// contract's goods are back, so it closes once it is paid. One return note on the new
    /// contract then brings everything home.
    /// </para>
    /// <para>
    /// Asked before or after the end, it is the same act: the carry-over is dated at the period's
    /// end either way, so nothing is charged as overdue for the gap and nothing given back.
    /// </para>
    /// </summary>
    public async Task<RentalResult<RentalContract>> ExtendAsNewAsync(
        Guid contractId, ExtendAsNewContractRequest r, Guid? userId, string? userName, CancellationToken ct = default)
    {
        if (r.DurationDays < 1) return RentalResult<RentalContract>.Invalid("An extension names its days, at least one.");

        var docs = await LoadAsync(contractId, ct);
        if (docs is null) return RentalResult<RentalContract>.NotFound("Contract not found.");
        if (NotOpen(docs.Contract) is string shut) return RentalResult<RentalContract>.Conflict(shut);
        if (docs.Contract.LockedAtUtc is null)
            return RentalResult<RentalContract>.Invalid("Nothing has gone out on this contract yet; edit it instead.");

        var zone = Zone();
        var regional = regionalSettings.Get();

        // What is still out, batch by batch, and the day each batch's period ends.
        var carried = Outstanding(docs, zone)
            .Select(b => (b.Line, b.EndsOn, BranchId: b.IssueBranchId, b.Qty))
            .ToList();
        if (carried.Count == 0)
            return RentalResult<RentalContract>.Invalid("Nothing is out on this contract, so there is nothing to extend.");

        await using var tx = await db.BeginWriteScopeAsync(ct);

        var now = DateTime.UtcNow;
        var old = docs.Contract;
        var extension = new RentalContract
        {
            CustomerId = old.CustomerId,
            BranchId = old.BranchId,
            Status = RentalContractStatus.Open,
            SignedAtUtc = r.SignedAtUtc ?? now,
            LockedAtUtc = now,
            Notes = TrimOrNull(r.Notes) is { } notes ? (notes.Length <= 500 ? notes : notes[..500]) : null,
            CreatedBy = userName,
            ExtendsContractId = old.Id,
            // The same signatory, site and identity: it is the same customer's same job, extended.
            SignatoryName = old.SignatoryName, SignatoryIdNumber = old.SignatoryIdNumber,
            SignatoryRequired = old.SignatoryRequired, SignatoryIdType = old.SignatoryIdType,
            SignatoryIdIssuer = old.SignatoryIdIssuer, SignatoryIdIssuedOn = old.SignatoryIdIssuedOn,
            SignatoryBirthDate = old.SignatoryBirthDate, SignedByAgent = old.SignedByAgent,
            SiteId = old.SiteId, SiteName = old.SiteName, SiteAddress = old.SiteAddress,
            // And the same terms: the extension prints the wording its contract was signed under,
            // and bills with or without VAT as it did; its lines carry the frozen rates over too.
            WordingKey = old.WordingKey,
            TaxMode = old.TaxMode
        };
        extension.Number = Numbered("CTR", await db.RentalContracts.CountAsync(ct) + 1);
        db.RentalContracts.Add(extension);

        // One line per line still out, at the quantity carried and the prices it was signed at.
        var map = new Dictionary<Guid, RentalContractLine>();
        foreach (var line in docs.Lines.Where(l => carried.Any(x => x.Line.Id == l.Id)).OrderBy(l => l.SortOrder))
        {
            var copy = new RentalContractLine
            {
                ContractId = extension.Id, ProductId = line.ProductId, Sku = line.Sku,
                NameSnapshot = line.NameSnapshot, NameArSnapshot = line.NameArSnapshot,
                Qty = carried.Where(x => x.Line.Id == line.Id).Sum(x => x.Qty),
                DurationDays = r.DurationDays,
                RentUnitPrice = line.RentUnitPrice, DamageUnitPrice = line.DamageUnitPrice,
                OverdueUnitPrice = line.OverdueUnitPrice, TaxRate = line.TaxRate, SortOrder = line.SortOrder
            };
            map[line.Id] = copy;
        }
        foreach (var (oldId, copy) in map)
        {
            var parent = docs.Lines.First(l => l.Id == oldId).ParentLineId;
            copy.ParentLineId = parent is Guid p && map.TryGetValue(p, out var parentCopy) ? parentCopy.Id : null;
            db.RentalContractLines.Add(copy);
        }
        var newLines = map.Values.ToList();
        var newEngineLines = newLines.Select(EngineLine).ToList();

        var nextReturn = await db.ReturnNotes.CountAsync(ct) + 1;
        var nextIssue = await db.IssueNotes.CountAsync(ct) + 1;
        // Under the version lock: a till pushing a credit sale may be adding to this debt.
        var customer = await db.LockedAsync<Customer>(old.CustomerId, ct);

        foreach (var group in carried.GroupBy(x => (x.EndsOn, x.BranchId)).OrderBy(g => g.Key.EndsOn))
        {
            var at = TimeZoneInfo.ConvertTimeToUtc(group.Key.EndsOn.ToDateTime(new TimeOnly(12, 0)), zone);

            var back = new ReturnNote
            {
                ContractId = old.Id, BranchId = old.BranchId, ReturnedAtUtc = at, UserId = userId, UserName = userName,
                Note = $"Report vers {extension.Number}", IsCarryOver = true, CustomerTransport = true,
                CountReturnDay = false, Number = Numbered("RET", nextReturn++)
            };
            foreach (var x in group.GroupBy(x => x.Line.Id))
                back.Lines.Add(new ReturnNoteLine { ReturnNoteId = back.Id, ContractLineId = x.Key, QtyOk = x.Sum(y => y.Qty) });
            db.ReturnNotes.Add(back);
            docs.ReturnNotes.Add(back);

            var outAgain = new IssueNote
            {
                ContractId = extension.Id, BranchId = group.Key.BranchId, IssuedAtUtc = at, UserId = userId, UserName = userName,
                Note = $"Report de {old.Number}", IsCarryOver = true, CustomerTransport = true,
                PeriodStartsOn = group.Key.EndsOn, Number = Numbered("ISS", nextIssue++)
            };
            var rent = 0m;
            var netByRate = new Dictionary<decimal, decimal>();
            foreach (var x in group.GroupBy(x => x.Line.Id))
            {
                var copy = map[x.Key];
                var qty = x.Sum(y => y.Qty);
                outAgain.Lines.Add(new IssueNoteLine { IssueNoteId = outAgain.Id, ContractLineId = copy.Id, Qty = qty });
                var lineRent = Engine.RentalPricing.Amount(
                    regional, copy.RentUnitPrice, Engine.RentalSettlement.DurationOf(EngineLine(copy), newEngineLines), qty);
                rent += lineRent;
                netByRate[copy.TaxRate] = netByRate.GetValueOrDefault(copy.TaxRate) + lineRent;
            }
            db.IssueNotes.Add(outAgain);

            // The new period is billed as any issue bills it: in full, when it starts.
            var tax = TaxOn(regional, netByRate);
            if (RentalPosting.Issue(outAgain.Id, outAgain.BranchId, extension.CustomerId, outAgain.Number, rent, userName,
                    StartOfDayUtc(group.Key.EndsOn, zone), tax)
                is PostingDraft invoice)
                // An old period that ended in a month since closed: the new one is booked on the first open day
                // (COR-E01, owner 2026-11-05), never into the closed books.
                await ledger.PostAsync(await BookClosings.OntoOpenBooksAsync(db, invoice, ct), ct);
            if (customer is not null && rent + tax != 0)
            {
                customer.Balance += rent + tax;
                await db.TouchAsync(customer, ct);
            }
        }

        // What the carry-over settles on this contract. Dated at each period's end, it charges no
        // overdue and gives nothing back; an extension signed the old way still charges the days it
        // added, as a return would.
        var after = Engine.RentalSettlement.Settle(Input(docs, zone), regional);
        foreach (var back in docs.ReturnNotes.Where(n => n.IsCarryOver && n.ContractId == old.Id && db.Entry(n).State == EntityState.Added))
        {
            var charges = StoreCharges(old, after, back.Id);
            db.RentalCharges.AddRange(charges);
            var owed = charges.Where(x => x.IsPosted).Sum(x => x.Amount);
            if (owed == 0) continue;
            var netOnReturn = new Dictionary<decimal, decimal>();
            foreach (var charge in charges.Where(x => x.IsPosted))
            {
                var rate = docs.Lines.FirstOrDefault(l => l.Id == charge.ContractLineId)?.TaxRate ?? 0m;
                netOnReturn[rate] = netOnReturn.GetValueOrDefault(rate) + charge.Amount;
            }
            var returnTax = TaxOn(regional, netOnReturn);
            if (RentalPosting.Return(back.Id, back.BranchId, old.CustomerId, back.Number,
                    charges.Where(x => x.IsPosted).Select(x => new ChargeAmount(x.Kind, x.Amount, back.Number)),
                    0m, userName, returnTax) is PostingDraft posted)
                await ledger.PostAsync(posted, ct);
            if (customer is not null)
            {
                customer.Balance += owed + returnTax;
                await db.TouchAsync(customer, ct);
            }
        }

        old.GoodsBackAtUtc ??= now;
        await db.SaveChangesAsync(ct);
        if (await RentalClosure.TryCloseAsync(db, old.Id, ct)) await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return RentalResult<RentalContract>.Success(extension);
    }

    // ---------- closing ----------

    /// <summary>
    /// The Admin closes a contract that is not paid in full (answer 15: "the super admin may close
    /// the contract without its full value, but states the reason"). Only once the goods are all
    /// back: a contract with goods still on site is not finished, whatever has been paid.
    /// </summary>
    public async Task<RentalResult<RentalContract>> CloseShortAsync(
        Guid contractId, string? reason, string? by, CancellationToken ct = default)
    {
        var text = TrimOrNull(reason);
        if (text is null) return RentalResult<RentalContract>.Invalid("Closing a contract short states the reason.");
        var contract = await db.RentalContracts.FirstOrDefaultAsync(x => x.Id == contractId, ct);
        if (contract is null) return RentalResult<RentalContract>.NotFound("Rental contract not found.");
        if (contract.Status == RentalContractStatus.Closed)
            return RentalResult<RentalContract>.Invalid($"Contract {contract.Number} is already closed.");
        if (contract.Status == RentalContractStatus.Cancelled)
            return RentalResult<RentalContract>.Invalid($"Contract {contract.Number} was cancelled.");
        if (contract.GoodsBackAtUtc is null)
            return RentalResult<RentalContract>.Invalid(
                $"Contract {contract.Number} still has goods out; it closes once they are all back.");

        await using var tx = await db.BeginWriteScopeAsync(ct);
        contract.Status = RentalContractStatus.Closed;
        contract.ClosedAtUtc = DateTime.UtcNow;
        contract.ClosedShortReason = text.Length <= 400 ? text : text[..400];
        contract.ClosedShortBy = TrimOrNull(by);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return RentalResult<RentalContract>.Success(contract);
    }

    /// <summary>
    /// Calls a contract off before anything has left (owner, 2026-10-06): "if there is an exit
    /// note the contract can't be cancelled". Nothing moves and nothing is charged before the first
    /// exit note, so there is nothing to undo; the row is kept with who, when and why, and from now
    /// on only <c>rentals.cancelled</c> sees it. A receipt already taken against it stays on the
    /// customer's account.
    /// </summary>
    public async Task<RentalResult<RentalContract>> CancelContractAsync(
        Guid contractId, string? reason, string? by, CancellationToken ct = default)
    {
        var contract = await db.RentalContracts.FirstOrDefaultAsync(x => x.Id == contractId, ct);
        if (contract is null) return RentalResult<RentalContract>.NotFound("Rental contract not found.");
        if (NotOpen(contract) is string shut) return RentalResult<RentalContract>.Conflict(shut);
        if (contract.LockedAtUtc is not null || await db.IssueNotes.AnyAsync(n => n.ContractId == contractId, ct))
            return RentalResult<RentalContract>.Conflict(
                $"Contract {contract.Number} has an exit note: goods have left, so it is not cancelled. Take them back on a return note.");
        var text = TrimOrNull(reason);
        if (text is null) return RentalResult<RentalContract>.Invalid("Say why the contract is cancelled.");

        await using var tx = await db.BeginWriteScopeAsync(ct);
        contract.Status = RentalContractStatus.Cancelled;
        contract.CancelledAtUtc = DateTime.UtcNow;
        contract.CancelReason = text.Length <= 300 ? text : text[..300];
        contract.CancelledBy = TrimOrNull(by);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return RentalResult<RentalContract>.Success(contract);
    }

    /// <summary>Why a contract takes no more documents, in words; null while it is open.</summary>
    private static string? NotOpen(RentalContract contract) => contract.Status switch
    {
        RentalContractStatus.Closed => "This contract is closed.",
        RentalContractStatus.Cancelled => $"Contract {contract.Number} was cancelled.",
        _ => null,
    };

    // ---------- attachments ----------

    /// <summary>The largest file kept with a contract: a phone photo of an identity card fits many times over.</summary>
    public const long MaxAttachmentBytes = 5 * 1024 * 1024;

    /// <summary>What a file kept with a contract may be. Only the identity scan today (answer 10a).</summary>
    public const string IdDocumentAttachment = "idDocument";

    /// <summary>Metadata of every file kept with a contract, oldest first; never the bytes.</summary>
    public async Task<IReadOnlyList<RentalAttachmentDto>> AttachmentsAsync(Guid contractId, CancellationToken ct = default) =>
        (await db.RentalContractAttachments.AsNoTracking()
            .Where(a => a.ContractId == contractId)
            .Select(a => new RentalAttachmentDto(a.Id, a.Kind, a.FileName, a.ContentType, a.Length, a.UploadedAtUtc))
            .ToListAsync(ct))
        .OrderBy(a => a.UploadedAtUtc).ToList();

    /// <summary>The media type a file's first bytes say it is, among the four kept; null for anything else.</summary>
    public static string? SniffContentType(byte[] content)
    {
        static bool StartsWith(byte[] bytes, int offset, params byte[] magic) =>
            bytes.Length >= offset + magic.Length && magic.Select((b, i) => bytes[offset + i] == b).All(x => x);

        if (StartsWith(content, 0, 0xFF, 0xD8, 0xFF)) return "image/jpeg";
        if (StartsWith(content, 0, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return "image/png";
        if (StartsWith(content, 0, (byte)'R', (byte)'I', (byte)'F', (byte)'F') && StartsWith(content, 8, (byte)'W', (byte)'E', (byte)'B', (byte)'P'))
            return "image/webp";
        if (StartsWith(content, 0, (byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-')) return "application/pdf";
        return null;
    }

    private static List<Engine.RentalExtension> Extensions(Documents docs, TimeZoneInfo zone) =>
        docs.Extensions.Select(e => new Engine.RentalExtension(
            e.Id, e.ContractLineId, e.ExtraDays, e.UnitPrice,
            Engine.RentalPricing.LocalDay(e.RaisedOnUtc, zone), e.Reason)).ToList();

    private static Engine.RentalContractInput Input(Documents docs, TimeZoneInfo zone) => new(
        docs.Lines.Select(EngineLine).ToList(),
        Batches(docs, zone),
        Entries(docs, zone),
        Extensions(docs, zone),
        docs.Contract.MinimumRentalDays);

    /// <summary>
    /// Document numbers, generated inside the write transaction.
    ///
    /// <para>
    /// Safe as a count because every write route here runs behind the subscription filter,
    /// which has already taken the version counter's row lock and therefore serialises writers
    /// across processes. The unique index on each number is the backstop: if that ever stops
    /// being true the insert fails loudly instead of quietly producing two notes with the same
    /// number on the customer's paperwork.
    /// </para>
    /// </summary>
    /// <summary>
    /// Tax on each rate's own total, summed — the rule every document in this system follows,
    /// stated once in <see cref="DocumentTax"/> so a rental note and an invoice raised over the
    /// same charges cannot round to two different figures.
    /// </summary>
    private static decimal TaxOn(RegionalSettings regional, IReadOnlyDictionary<decimal, decimal> netByRate) =>
        DocumentTax.On(regional, netByRate);

    private static string Numbered(string prefix, int next) => $"{prefix}-{next:D6}";
}
