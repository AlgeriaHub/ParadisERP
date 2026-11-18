namespace AlgeriaPos.Application.Rentals;

/// <summary>
/// The rules that say whether a document may be raised at all. They are separate from the
/// arithmetic on purpose: <see cref="RentalSettlement"/> answers "what does this cost", and
/// these answer "is this allowed", which is the question a screen has to ask before saving.
///
/// <para>
/// The <c>Problem</c> methods return a short reason code or null, the way
/// <c>GiftCardRules.Problem</c> does, so the caller translates it and nothing here holds text.
/// </para>
/// </summary>
public static class RentalRules
{
    /// <summary>
    /// A contract locks as soon as its first issue note is raised: after that the prices and
    /// quantities on it have been signed and acted on, and anything that has to change is a new
    /// document — an extension, a return note — never an edit.
    /// </summary>
    public static bool IsLocked(IEnumerable<IssueBatch> issues) => issues.Any();

    /// <summary>Rejects a line whose figures cannot produce honest money.</summary>
    public static void Validate(RentalLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Qty <= 0) throw new ArgumentException("A contract line needs a quantity above zero.");
        if (line.RentUnitPrice < 0 || line.DamageUnitPrice < 0 || line.OverdueUnitPrice < 0)
            throw new ArgumentException("Rental prices cannot be negative.");

        // An overdue day cheaper than a rented day would pay the customer to keep the goods,
        // and every contract would quietly run long.
        if (line.OverdueUnitPrice < line.RentUnitPrice)
            throw new ArgumentException("The overdue price cannot be below the rental price.");

        if (line.IsAccessory)
        {
            // Nobody hires the couplers — but if forty come back missing they are charged, so a
            // zero compensation price here is the one mistake that cannot be recovered later.
            if (line.RentUnitPrice != 0)
                throw new ArgumentException("An accessory line rents at zero.");
            if (line.DamageUnitPrice <= 0)
                throw new ArgumentException("An accessory line needs a total-damage price.");
            return;
        }

        if (line.DurationDays < 1) throw new ArgumentException("A rental line needs a duration of at least one day.");
    }

    /// <summary>
    /// Why a negotiated overdue price may not be signed, or null: the admin's multiplier is a
    /// <b>floor</b> (docs/07 §3.4, accepted 2026-09-24).
    ///
    /// <para>
    /// The derived figure — hire price × the multiplier in force, the item's own when it has one
    /// — is the least the company charges for a late day. A manager may negotiate <i>upwards</i>
    /// from it; below it, the figure the admin set would be a suggestion, and every contract
    /// would quietly be signed at 1.0×. A price left blank is derived and can never be below.
    /// </para>
    /// </summary>
    public static string? OverduePriceProblem(decimal? signedOverduePrice, decimal floor) =>
        signedOverduePrice is decimal signed && signed < floor ? "overdueBelowFloor" : null;

    /// <summary>The multiplier an admin may set: never below 1, never absurd.</summary>
    public static void Validate(RentalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.OverdueMultiplier < 1)
            throw new ArgumentException("The overdue multiplier cannot be below 1.");
        if (settings.OverdueMultiplier > 10)
            throw new ArgumentException("The overdue multiplier cannot be above 10.");
        if (RentalDays.CutoffProblem(settings.DayCutoffHour) is not null)
            throw new ArgumentException("The day cutoff hour must be between 0 and 23, or left blank to keep the delivery day and never count the return day.");
        if (settings.MinimumDays is < 0 or > 366)
            throw new ArgumentException("The minimum rental days must be between 0 and 366.");
    }

    /// <summary>Why this quantity cannot go out, or null. Issues never exceed the contracted quantity.</summary>
    public static string? IssueProblem(RentalLine line, decimal alreadyIssued, decimal qty)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (qty <= 0) return "quantityRequired";
        if (alreadyIssued + qty > line.Qty) return "exceedsContracted";
        return null;
    }

    /// <summary>
    /// Why this return cannot be taken, or null. A return bigger than what is still out is a
    /// counting mistake, and it has to stop at the screen: once it is saved the FIFO match has
    /// nothing to charge it against.
    /// </summary>
    public static string? ReturnProblem(ReturnEntry note, decimal issuedQty, decimal alreadySettled)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (note.QtyOk < 0 || note.QtyPartialDamage < 0 || note.QtyTotalDamage < 0 || note.QtyMissing < 0)
            return "negativeQuantity";
        if (note.SettledQty <= 0) return "quantityRequired";
        if (note.MaintenanceValue < 0) return "negativeMaintenance";
        if (note.MaintenanceValue > 0 && note.QtyPartialDamage <= 0) return "maintenanceNeedsDamagedUnits";
        if (alreadySettled + note.SettledQty > issuedQty) return "exceedsIssued";
        return null;
    }

    /// <summary>
    /// An extension signed after the period has already run out. It is allowed — sometimes the
    /// paperwork follows the handshake — but it turns days that were already overdue into
    /// rented days, so it is a manager's decision and it is written down.
    /// </summary>
    public static bool IsRetroactive(RentalExtension extension, DateOnly periodEndsOn)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return extension.RaisedOn > periodEndsOn;
    }

    /// <summary>
    /// Why this extension cannot be signed, or null.
    ///
    /// <para>
    /// Left open to everyone, every overdue becomes an extension and the overdue line loses its
    /// meaning — which is the whole reason the two are separate documents.
    /// </para>
    /// </summary>
    public static string? ExtensionProblem(
        RentalExtension extension, DateOnly periodEndsOn, bool isManager)
    {
        ArgumentNullException.ThrowIfNull(extension);
        if (extension.ExtraDays < 1) return "daysRequired";
        if (extension.UnitPrice < 0) return "negativePrice";
        if (!IsRetroactive(extension, periodEndsOn)) return null;
        if (!isManager) return "retroactiveNeedsManager";
        if (string.IsNullOrWhiteSpace(extension.Reason)) return "retroactiveNeedsReason";
        return null;
    }

    /// <summary>
    /// Why the first issue note may not be raised for want of a signatory, or null.
    ///
    /// <para>
    /// The rule bites once, at the first note — that is when the contract locks and the paper
    /// matters — so it is silent once <paramref name="locked"/>. It is also silent when the
    /// contract was raised before the rule existed (<paramref name="required"/> false): those
    /// contracts were valid under the old rules, and refusing them now would stop goods a
    /// customer already agreed to hire.
    /// </para>
    /// <para>
    /// The name is asked on its own, first, so a contract with nobody named gets the one sentence
    /// that says so. The rest of the identity — document type, number, issuer, issue date, birth
    /// date — became mandatory with the owner's answer 10 (2026-09-30) and is asked next, at the
    /// same moment: see <see cref="SignatoryIdentityMissing"/>.
    /// </para>
    /// </summary>
    public static string? SignatoryProblem(bool locked, bool required, string? name)
    {
        if (locked || !required) return null;
        return string.IsNullOrWhiteSpace(name) ? "signatoryRequired" : null;
    }

    /// <summary>
    /// The parts of the signatory's identity the first issue note still lacks, in the order the
    /// paper prints them; empty when nothing is missing or the rule does not apply.
    ///
    /// <para>
    /// The owner's answer 10 (2026-09-30) made the rest of the identity mandatory; his answer of
    /// 2026-10-02 kept only which document and its number, and made the issuer, the issue date and
    /// the birth date optional but checked when filled. It bites where the name does —
    /// once, at the first note, on a contract raised under the signatory rule — and is asked after
    /// the name, so a contract with nobody named still gets the one sentence that says so. The
    /// codes are field names (<c>idType</c>, <c>idNumber</c>, <c>idIssuer</c>, <c>idIssuedOn</c>,
    /// <c>birthDate</c>) so the caller can name each in words.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> SignatoryIdentityMissing(bool locked, bool required, SignatoryIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (locked || !required) return [];
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(identity.IdType)) missing.Add("idType");
        if (string.IsNullOrWhiteSpace(identity.IdNumber)) missing.Add("idNumber");
        // The issuer, the issue date and the birth date are optional since the owner's answer of
        // 2026-10-02; when filled in they are checked by IdDocumentTypes.IdentityProblem instead.
        return missing;
    }

    /// <summary>A document type the paper knows, or blank; anything else is a typo, not a new kind of card.</summary>
    public static string? IdTypeProblem(string? idType) =>
        string.IsNullOrWhiteSpace(idType) || RentalDays.IdDocumentTypes.Contains(idType.Trim()) ? null : "idTypeUnknown";

    /// <summary>
    /// What an issue or return note lacks of the driver and the truck, in the order the paper
    /// prints them (<c>driverName</c>, <c>driverPhone</c>, <c>truckNumber</c>); empty when complete.
    ///
    /// <para>
    /// "إجباري" on the owner's own note, and answer 16: mandatory on both notes, and "yes, it
    /// refuses to save" — when the branch transports. Since 2026-10-02 a customer who carries the
    /// goods himself is the exception: the note is marked <c>CustomerTransport</c>, says so in
    /// words, and this rule is not asked.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> TransportMissing(string? driverName, string? driverPhone, string? truckNumber)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(driverName)) missing.Add("driverName");
        if (string.IsNullOrWhiteSpace(driverPhone)) missing.Add("driverPhone");
        if (string.IsNullOrWhiteSpace(truckNumber)) missing.Add("truckNumber");
        return missing;
    }

    /// <summary>An item's fixed replacement value (answer 13): absent, or a price that is not negative.</summary>
    public static string? DamageValueProblem(decimal? value) => value is < 0 ? "negativeDamageValue" : null;

    /// <summary>
    /// The damage price a contract line starts from: the one typed on the line wins, and a line
    /// that names none (zero) takes the item's fixed replacement value when it has one.
    /// </summary>
    public static decimal DamagePriceFor(decimal typedOnLine, decimal? itemValue) =>
        typedOnLine > 0 ? typedOnLine : itemValue is decimal value && value > 0 ? value : typedOnLine;

    /// <summary>
    /// The period an extension is measured against: the first batch still out is the one about
    /// to expire, so that is the deadline the signature has to beat.
    /// </summary>
    public static DateOnly? SoonestPeriodEnd(IEnumerable<IssueBatch> openBatches, int entitledDays)
    {
        DateOnly? soonest = null;
        foreach (var batch in openBatches)
        {
            var end = RentalPricing.PeriodEndsOn(batch.IssuedOn, entitledDays);
            if (soonest is null || end < soonest) soonest = end;
        }
        return soonest;
    }
}

/// <summary>
/// The signatory's identity as the paper contract records it — the five fields the first issue
/// note asks for beside the name (see <see cref="RentalRules.SignatoryIdentityMissing"/>).
/// </summary>
public sealed record SignatoryIdentity(
    string? IdType, string? IdNumber, string? IdIssuer, DateOnly? IdIssuedOn, DateOnly? BirthDate);

/// <summary>
/// A customer's site (answer 9): mandatory on every contract raised from now on, and one customer
/// may have several. Only the name is required; it is what the paper prints.
/// </summary>
public static class CustomerSiteRules
{
    public const int NameMaxLength = 120;
    public const int AddressMaxLength = 300;
    public const int PhoneMaxLength = 40;

    public static string? Problem(string? name, string? address, string? phone)
    {
        if (string.IsNullOrWhiteSpace(name)) return "siteNameRequired";
        if (name.Trim().Length > NameMaxLength) return "siteNameTooLong";
        if ((address?.Trim().Length ?? 0) > AddressMaxLength) return "siteAddressTooLong";
        if ((phone?.Trim().Length ?? 0) > PhoneMaxLength) return "sitePhoneTooLong";
        return null;
    }
}

/// <summary>
/// The deposit (§5.6, docs/07 §3.3): a figure signed on the contract and held on the customer's
/// behalf.
///
/// <para>
/// What the switch does: <b>recorded and shown</b>. While it is on, the amount agreed is stored
/// on the contract, frozen with the prices at the first issue note, printed beside them, and
/// stated as owed back when the contract closes. While it is off the deposit is invisible — no
/// field, no zero line, no report column — and turning it off hides a stored figure without
/// erasing it.
/// </para>
/// <para>
/// What it never does: compute. A deposit is a liability towards the customer, not income and
/// not a payment against charges, so it is never netted off what a contract owes, never enters
/// the customer's balance and never reaches <see cref="RentalSettlement.Relieve"/>. Nothing
/// posts either — collecting it and refunding it each need a money document that does not exist
/// yet, and the account it will sit on (419 or 165) is the accountant's call; a figure on a
/// contract is a promise, not money. Closing therefore <i>states</i> the deposit to return and
/// never waits for it.
/// </para>
/// </summary>
public static class RentalDeposit
{
    /// <summary>The deposit held on a contract: zero, and invisible, while the switch is off.</summary>
    public static decimal Held(RentalSettings settings, decimal contractDeposit)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.DepositEnabled ? Math.Max(0m, contractDeposit) : 0m;
    }

    /// <summary>
    /// What is owed back to the customer: the whole deposit once the contract is closed, nothing
    /// before. Not a payment — there is no document that pays it yet — but a visible, owed figure,
    /// the stance already taken on the early-return refund. Zero while the switch is off.
    /// </summary>
    public static decimal ToReturn(RentalSettings settings, decimal contractDeposit, bool isClosed) =>
        isClosed ? Held(settings, contractDeposit) : 0m;

    /// <summary>Whether a deposit field, column or line may appear anywhere at all.</summary>
    public static bool IsVisible(RentalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.DepositEnabled;
    }
}
