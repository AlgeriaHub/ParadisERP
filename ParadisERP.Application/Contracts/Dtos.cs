namespace ParadisERP.Application.Contracts;

// ---------- auth ----------

/// <param name="TwoFactorCode">The authenticator's six digits, or a recovery code, when the account has two-step login on.</param>
/// <param name="TrustToken">A "trust this computer" token an earlier sign-in here answered with; it stands in for the code.</param>
/// <param name="RememberDevice">Answer with such a token, good for 30 days, once the code is right.</param>
public record LoginRequest(string Username, string Password, string? DeviceCode,
    string? TwoFactorCode = null, string? TrustToken = null, bool RememberDevice = false);

/// <summary>
/// A signed-in person changing their own password (owner, 2026-10-05: "the user after login can
/// change password"). The current one is asked for again, so a till left signed in is not enough to
/// take the account over. Answered with a fresh <see cref="LoginResponse"/>: the change revokes every
/// session the account held, the caller's own token among them.
/// </summary>
public record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public record LoginResponse(string Token, DateTime ExpiresAtUtc, UserDto User, string DeviceCode,
    ParadisERP.Application.Subscriptions.SubscriptionAccess? Subscription = null,
    ParadisERP.Application.Regional.RegionalSettings? Regional = null,
    // Revision per uploaded language pack the company enables; tills download changed ones.
    IReadOnlyDictionary<string, long>? LanguagePacks = null,
    ParadisERP.Application.Integrations.IntegrationsPublicView? Integrations = null,
    // The branch of the terminal the session was opened on, so a till's top bar shows its branch.
    Guid? DeviceBranchId = null,
    // The highest receipt sequence the server holds for that till: a browser continues after it, so
    // a cashier who opens their till on a second computer does not reuse a receipt number.
    long LastLocalSeq = 0,
    // Two-step login: the "trust this computer" token, when the sign-in asked to be remembered.
    string? TwoFactorTrust = null);

// ---------- the signed-in user's own account (owner, 2026-11-11) ----------

/// <summary>The current password, asked again before two-step login is changed.</summary>
public record PasswordConfirmRequest(string? Password);

/// <summary>A code from the authenticator app (or, where accepted, a recovery code).</summary>
public record TwoFactorCodeRequest(string? Code);

public record TwoFactorStatusDto(bool Enabled, DateTime? EnabledAtUtc, int RecoveryCodesLeft);

/// <summary>
/// The key the setup offers: as the QR code's address and as text to type, with the names the app
/// will show. Nothing is on until a code from it is sent back.
/// </summary>
public record TwoFactorSetupDto(string Secret, string ProvisioningUri, string Issuer, string Account);

/// <summary>Recovery codes, shown once: the server keeps only their hashes.</summary>
public record RecoveryCodesDto(IReadOnlyList<string> Codes);

/// <summary>
/// "My profile": who the account is, read-only, and its photo, which the user may change. The
/// employee part is the HR record the account belongs to, null when none is linked.
/// </summary>
public record ProfileDto(
    Guid Id, string FullName, string Username, string? LoginId, string Role, string? RoleName,
    string? BranchNameAr, string? BranchNameFr, string? BranchNameEn,
    ProfileEmployeeDto? Employee,
    // A short tag that changes with the photo, for its link; null when there is none.
    string? PhotoVersion,
    TwoFactorStatusDto TwoFactor);

public record ProfileEmployeeDto(string Number, string? JobTitle, string? Phone, string? Email, DateOnly HiredOn);
/// <param name="BranchId">The branch this user works at: their assigned branch, or the main branch for a legacy row with none.</param>
/// <param name="BranchLocked">True for everyone but the Admin: no branch choice on any screen (Application/Security/BranchLock).</param>
/// <param name="Permissions">What the user holds — grants plus the Admin's own — so a screen can hide what the server would refuse.</param>
/// <param name="Capabilities">
/// What the server's own decision lets this user do (Domain/Capabilities), never granted: a screen
/// offers an action by reading this, not by naming roles. A session from before it existed has none,
/// which a screen reads as "not allowed".
/// </param>
/// <param name="Role">The legacy projection of the role's scope (Admin, Manager, Cashier), for older screens and tills.</param>
/// <param name="PermissionKeys">
/// Every catalogue key the server's decision gives them (AUTH-1 Step 6): the screens decide what to offer from
/// this, never from <paramref name="Role"/>. Presentation only; the server decides every request again.
/// </param>
public record UserDto(Guid Id, string Username, string FullName, string Role,
    Guid? BranchId = null, bool BranchLocked = false, IReadOnlyList<string>? Permissions = null,
    IReadOnlyList<string>? Capabilities = null,
    Guid? RoleId = null, string? RoleName = null, string? Scope = null,
    IReadOnlyList<string>? PermissionKeys = null);

// ---------- sync: pull ----------

// HasMore is true when the server truncated this batch and the client should pull again.
public record PullResponse(
    long Cursor,
    bool HasMore,
    DateTime ServerTimeUtc,
    StoreDto? Store,
    IReadOnlyList<BranchDto> Branches,
    IReadOnlyList<CategoryDto> Categories,
    IReadOnlyList<ProductDto> Products,
    IReadOnlyList<CustomerDto> Customers,
    IReadOnlyList<PosUserDto> Users,
    ParadisERP.Application.Subscriptions.SubscriptionAccess? Subscription = null,
    // Sent on every pull (it is small), so a changed profile reaches tills without a cursor bump.
    ParadisERP.Application.Regional.RegionalSettings? Regional = null,
    IReadOnlyDictionary<string, long>? LanguagePacks = null,
    IReadOnlyList<GiftCardDto>? GiftCards = null,
    ParadisERP.Application.Integrations.IntegrationsPublicView? Integrations = null,
    // Replicated because the till needs the tender list at the moment of payment, offline
    // included. Small and rarely changed, so it rides the same cursor as the catalog.
    IReadOnlyList<PaymentMethodDto>? PaymentMethods = null);

/// <summary>
/// A tender as the till needs to see it: what to call it, what it does, and whether taking it
/// means the money has actually arrived.
/// </summary>
public record PaymentMethodDto(
    Guid Id, string Key, string NameAr, string NameFr, string NameEn,
    string Behavior, bool RequiresConfirmation, bool AffectsCashDrawer,
    bool RequiresReference, bool RequiresInstrumentDetails,
    bool IsBuiltIn, bool IsActive, int SortOrder, long Version, bool IsDeleted);

/// <summary>
/// A gift card as a till holds it. Since the security review of 2026-10-10 the till never holds the
/// code itself — a code is spent like cash, and every till's IndexedDB held every card's code. It
/// holds <paramref name="CodeHash"/> (SHA-256 of the normalised code, hex) to find the card a
/// customer hands over, and in <paramref name="Code"/> only a hint for the receipt ("…7KQ2").
/// </summary>
public record GiftCardDto(
    Guid Id, string Code, decimal InitialAmount, decimal Balance, bool IsActive,
    DateTime? ExpiresAtUtc, Guid? CustomerId, long Version, bool IsDeleted, string? CodeHash = null);

public record StoreDto(
    Guid Id, string NameAr, string NameFr, string NameEn, string Address, string Phone,
    IReadOnlyDictionary<string, string> FiscalIds,
    string ReceiptFooter,
    bool RequireCustomerEmail, bool RequireCustomerBirthDate, bool RequireCustomerPhone,
    bool RequireCustomerAddress, bool RequireCustomerTaxId,
    long Version, bool IsDeleted,
    bool LoyaltyEnabled = false, decimal LoyaltyPointsPerUnit = 0, decimal LoyaltyPointValue = 0,
    int LoyaltyMinRedeemPoints = 0,
    // The company's interface face; null follows the country profile. Trailing and defaulted so
    // a till built before it keeps parsing.
    string? UiFont = null,
    string AddressAr = "", string AddressFr = "", string AddressEn = "",
    string ReceiptFooterAr = "", string ReceiptFooterFr = "", string ReceiptFooterEn = "");

public record BranchDto(
    Guid Id, string NameAr, string NameFr, string NameEn, string Address, string Phone,
    bool IsMain, bool AllowNegativeStock, bool IsActive, long Version, bool IsDeleted,
    // Whether the branch is trading. A branch still in setup accepts opening balances; once
    // it is open, new goods come through the warehouse like everywhere else.
    bool IsOpenForOperation = false,
    string AddressAr = "", string AddressFr = "", string AddressEn = "",
    // The branch's own fiscal identifiers (commercial register, NIF, NIS, article number),
    // keyed like the store's; null or empty means the company's are printed instead.
    Dictionary<string, string>? FiscalIds = null,
    // The link to the branch's logo (signed-in only, StoreLogo.BranchUrl); a till fetches it once
    // and keeps a copy to draw its top bar offline. Null falls back to the main branch's
    // (core/branch-face.ts).
    string? LogoUrl = null,
    // A warehouse holds stock and never sells (owner, 2026-10-05). IsMain: the main one of its kind.
    bool IsWarehouse = false,
    // The location's three-digit code (100–999), given by the server and fixed for life (owner,
    // 2026-10-05); the customer and employee codes carry it. Trailing and defaulted for older tills.
    string Code = "");

public record CategoryDto(
    Guid Id, string NameAr, string NameFr, string NameEn, string Color, int SortOrder,
    long Version, bool IsDeleted);

/// <summary>
/// StockQty and ReorderLevel are scoped to whichever branch the pulling terminal belongs to
/// (resolved server-side from its device code) — a terminal only ever needs to know its own
/// branch's stock, never every branch's, so the wire shape stays flat.
/// <para>
/// <c>HiddenAtTill</c> (manufacturing, Phase 1): raw materials, consumables and semi-finished goods
/// are the workshop's, so the till leaves them out of its grid, its search and its barcode lookup. The
/// rule itself stays on the server (<c>ManufacturingRoles.ForSale</c>: the role, or the product marked for
/// sale as well); a till only reads the flag,
/// and a row cached before it existed reads as false — shown — until its next pull. A role change stamps
/// a new product version, so that pull comes.
/// </para>
/// </summary>
public record ProductDto(
    Guid Id, string Sku, string? Barcode, string NameAr, string NameFr, string NameEn,
    Guid? CategoryId, decimal Price, decimal TaxRate, string Unit, bool TrackStock,
    decimal StockQty, decimal ReorderLevel, bool IsActive, long Version, bool IsDeleted,
    bool IsRentable = false, decimal AssetUnitValue = 0, bool HiddenAtTill = false);

/// <summary>
/// The customer as a till sees it. <c>Kind</c>, <c>IdDocumentNumber</c> and the representative
/// history stay off this record on purpose: nothing on a terminal reads them (the register uses
/// the name and the points, the receipt prints the name), and a replicated field nothing reads is
/// dead wiring. The day a receipt has to say شركة, append <c>Kind</c> — and only <c>Kind</c> — as
/// a trailing defaulted parameter, here and in the till's <c>models.ts</c>, together with that
/// consumer. A test asserts by reflection that none of the three is here.
/// </summary>
public record CustomerDto(
    Guid Id, string Code, string FullName, string? Phone, string? Address, string? TaxId,
    string? Email, DateTime? BirthDate,
    decimal CreditLimit, decimal Balance, long Version, bool IsDeleted, int LoyaltyPoints = 0);

/// <summary>
/// Cashier record replicated to the terminal. Carries the BCrypt PIN hash only — never the
/// web password hash — so a terminal can authenticate a cashier with no network.
/// </summary>
public record PosUserDto(
    Guid Id, string Username, string FullName, string Role, string PinHash,
    bool IsActive, long Version, bool IsDeleted);

// ---------- sync: push ----------

// Nullable collections: System.Text.Json does not enforce C# nullability on record
// parameters, so a body that omits "sales" really does arrive as null.
/// <param name="DeviceCode">
/// What the till believes its code is. <b>The server ignores it</b> and uses the terminal the
/// caller's token was issued for; it stays on the contract only so existing tills keep sending
/// a body the server accepts. Anything that branches on it is a way for one till to write into
/// another branch's books — see <c>SyncService.PushAsync</c>.
/// </param>
public record PushRequest(string DeviceCode, IReadOnlyList<SalePush>? Sales);

public record SalePush(
    Guid Id,
    string Reference,
    long LocalSeq,
    DateTime SoldAtUtc,
    Guid? CustomerId,
    Guid CashierUserId,
    string CashierName,
    decimal SubTotalHt,
    decimal TaxTotal,
    decimal DiscountTotal,
    decimal GrandTotalTtc,
    decimal PaidAmount,
    decimal ChangeAmount,
    string PaymentMethod,
    string Status,
    Guid? RefundOfSaleId,
    string? Note,
    IReadOnlyList<SaleLinePush>? Lines,
    IReadOnlyList<SalePaymentPush>? Payments,
    decimal RoundingAdjustment = 0,
    // Defaults to taxed so a till that has not been updated yet keeps pushing what it always
    // pushed, rather than silently producing untaxed invoices.
    string? TaxMode = null);

public record SaleLinePush(
    Guid Id, Guid ProductId, string Sku, string NameSnapshot, decimal Qty,
    decimal UnitPrice, decimal TaxRate, decimal LineDiscount,
    decimal LineTotalTtc, decimal LineTaxAmount);

/// <summary>
/// <paramref name="Method"/> stays the built-in name the till has always sent, so older
/// terminals keep working unchanged. <paramref name="MethodKey"/> is the definition the
/// payment was actually taken under and is what operator-defined tenders travel as; when it
/// is absent the server derives it from <paramref name="Method"/>.
/// </summary>
public record SalePaymentPush(
    Guid Id, string Method, decimal Amount, string? Reference,
    string? MethodKey = null,
    string? InstrumentNumber = null,
    string? InstrumentBank = null,
    DateTime? InstrumentDueDate = null);

/// <summary>
/// <paramref name="Code"/> is a stable name the till can translate — <c>duplicate_receipt_number</c>,
/// <c>concurrent_update</c>, <c>server_error</c> (docs/07 §2.4). Trailing and null on older
/// servers, so a till that meets one during a rolling upgrade falls back to <paramref name="Message"/>,
/// which stays a safe sentence written by us and never the database's own words.
/// </summary>
public record PushResultItem(Guid Id, string Status, string? Message, string? Code = null);

public record PushResponse(
    long Cursor,
    DateTime ServerTimeUtc,
    int Accepted,
    int Duplicates,
    int Rejected,
    IReadOnlyList<PushResultItem> Results);

// ---------- catalog admin ----------

public record UpsertProductRequest(
    string Sku, string? Barcode, string NameAr, string NameFr, string NameEn,
    Guid? CategoryId, decimal Price, decimal CostPrice, decimal TaxRate, string Unit,
    bool TrackStock, bool IsActive,
    // Hired out rather than sold: it goes out and comes back, so only these carry the
    // on-rent, maintenance and scrap balances. Trailing and defaulted, so a caller that
    // predates renting keeps working and simply marks nothing rentable.
    bool IsRentable = false,
    // What one unit is carried at in the asset register — not its cost, and not what a
    // customer is charged for losing it.
    decimal AssetUnitValue = 0);

public record UpsertCategoryRequest(string NameAr, string NameFr, string NameEn, string Color, int SortOrder);

// ---------- treasury ----------

/// <summary>
/// Behaviour is deliberately absent: an operator-added tender is always generic. Letting one
/// declare itself Credit or GiftCard would hand it side effects — moving a customer balance,
/// spending a card — that the rest of the system does not know it has.
/// </summary>
public record UpsertPaymentMethodRequest(
    string Key, string NameAr, string NameFr, string NameEn,
    bool RequiresConfirmation, bool RequiresReference, bool RequiresInstrumentDetails,
    bool IsActive, int SortOrder);

public record PendingPaymentDto(
    Guid Id, Guid SaleId, string SaleReference, DateTime SoldAtUtc,
    Guid? CustomerId, string? CustomerName,
    string MethodKey, decimal Amount, string Status,
    string? InstrumentNumber, string? InstrumentBank, DateTime? InstrumentDueDate,
    DateTime? ConfirmedAtUtc, string? StatusNote);

public record SettlePaymentRequest(string Status, string? Note);

public record CashBoxDto(
    Guid Id, Guid BranchId, string NameAr, string NameFr, string NameEn, bool IsMain, decimal Balance);

public record CashMovementDto(
    Guid Id, Guid CashBoxId, string Direction, decimal Amount, decimal BalanceAfter,
    string Kind, Guid? RelatedCashBoxId, string? UserName, string? Reference, string? Note,
    DateTime OccurredAtUtc);

public partial record RemittanceRequest(
    Guid FromCashBoxId, Guid ToCashBoxId, decimal Amount, string? Reference, string? Note);

/// <summary>
/// <paramref name="Kind"/> travels as a string ("Person" / "Company" / "Unspecified"), parsed
/// case-insensitively by <c>CustomerRules.TryParseKind</c>; <paramref name="Representative"/>
/// is the company's first authorised representative, accepted only while the company has none
/// (a later change goes through <c>/customers/{id}/representatives</c> so the history is kept).
/// <paramref name="Code"/> is <b>ignored</b>: the server draws the code when a customer is created
/// and never changes it afterwards (owner, 2026-10-05; <c>CodeSeries</c>). It stays on the
/// contract, optional, only so a client that still sends one is not refused.
/// <paramref name="BranchId"/> is the location the customer is registered at, whose code goes into
/// the customer code — read on create only, and only from a company-scoped caller (the Admin's
/// working branch). Anyone else registers at their own branch whatever is sent.
/// </summary>
public record UpsertCustomerRequest(
    string? Code, string FullName, string? Phone, string? Address, string? TaxId,
    string? Email, DateTime? BirthDate, decimal CreditLimit,
    string Kind = "Unspecified", string? IdDocumentNumber = null,
    RepresentativeRequest? Representative = null,
    // Which paper the id number is from ("nationalCard" | "drivingLicence" | "passport"), who
    // issued it and when — the rest of what the owner's paper contract records about a person.
    string? IdDocumentType = null, string? IdDocumentIssuer = null, DateOnly? IdDocumentIssuedOn = null,
    Guid? BranchId = null,
    // A person's name in two parts (owner, 2026-10-06), first name first: when either is sent for a
    // person, both are required and FullName is composed from them (CustomerRules.Names). Ignored for
    // a company, which has one legal name. Neither sent — an older client — files FullName whole as
    // the family name, as the migration filed everyone before.
    string? FirstName = null, string? FamilyName = null,
    // A company's other fiscal identifiers (RC, AI, NIS…), keyed by the country profile's keys.
    // Null leaves the stored ones as they are — a till's quick-create never sends them — and a
    // person is cleared whatever is sent.
    IReadOnlyDictionary<string, string>? FiscalIds = null,
    // The balance the customer starts with (owner, 2026-10-10, go-live). Read on create only — an
    // edit never sends or changes it; setting it later has its own route (customers.opening_balance).
    CustomerOpeningBalanceRequest? OpeningBalance = null);

/// <summary>
/// An opening balance: a positive amount and who owes whom
/// (<see cref="ParadisERP.Application.Customers.OpeningBalanceRules.OwesUs"/> or <c>weOwe</c>).
/// <paramref name="On"/> null means today in the company's calendar.
/// </summary>
public record CustomerOpeningBalanceRequest(decimal Amount, string Direction, DateOnly? On = null);

/// <summary>A correction of an opening balance: an amount, a direction and the reason, required.</summary>
public record OpeningCorrectionRequest(decimal Amount, string Direction, string Reason, DateOnly? On = null);

/// <summary>One correction as the customer card and the statement show it; <paramref name="Amount"/> is signed.</summary>
public record OpeningCorrectionDto(
    Guid Id, string Number, Guid CustomerId, decimal Amount, DateOnly EffectiveOn, string Reason,
    string? CreatedBy, DateTime CreatedAtUtc);

public record RepresentativeRequest(
    string FullName, string? IdDocumentNumber = null, string? Phone = null, string? Note = null,
    string? IdDocumentType = null, string? IdDocumentIssuer = null, DateOnly? IdDocumentIssuedOn = null);

/// <summary><paramref name="FromUtc"/> null means now.</summary>
public record ChangeRepresentativeRequest(
    string FullName, string? IdDocumentNumber = null, string? Phone = null, string? Note = null,
    DateTime? FromUtc = null,
    string? IdDocumentType = null, string? IdDocumentIssuer = null, DateOnly? IdDocumentIssuedOn = null);

public record RepresentativeDto(
    Guid Id, Guid CustomerId, string FullName, string? IdDocumentNumber, string? Phone, string? Note,
    DateTime FromUtc, DateTime? ToUtc, string? CreatedBy, DateTime CreatedAtUtc,
    string? IdDocumentType = null, string? IdDocumentIssuer = null, DateOnly? IdDocumentIssuedOn = null);

/// <summary>
/// A place a customer takes hired goods to. Back-office only, like the representative history:
/// <c>/api/customers/{customerId}/sites</c>.
/// </summary>
/// <summary>A copy of a customer's identity document: what it is, never the bytes.</summary>
public record CustomerAttachmentDto(
    Guid Id, string Kind, string FileName, string ContentType, long Length, DateTime UploadedAtUtc, string? UploadedBy);

public record CustomerSiteDto(Guid Id, Guid CustomerId, string Name, string? Address, string? Phone, bool IsActive);

public record UpsertCustomerSiteRequest(string Name, string? Address, string? Phone, bool IsActive = true);

/// <summary>
/// The back office's customer: what a till gets plus the kind, the person's id number and the
/// company's <i>current</i> representative (null for a person, or for a company with none yet).
/// </summary>
public record AdminCustomerDto(
    Guid Id, string Code, string FullName, string? Phone, string? Address, string? TaxId,
    string? Email, DateTime? BirthDate, decimal CreditLimit, decimal Balance, int LoyaltyPoints,
    long Version, string Kind, string? IdDocumentNumber, RepresentativeDto? Representative,
    string? IdDocumentType = null, string? IdDocumentIssuer = null, DateOnly? IdDocumentIssuedOn = null,
    // The two parts a person's FullName is composed of; null for a company, and no first name for a
    // person filed before the parts existed (the whole name is the family name).
    string? FirstName = null, string? FamilyName = null,
    IReadOnlyDictionary<string, string>? FiscalIds = null,
    // The opening balance, signed (positive: the customer owes), and whether it is fixed: a set
    // timestamp means it was written and only a correction changes it.
    decimal OpeningBalance = 0m, DateOnly? OpeningBalanceOn = null,
    DateTime? OpeningBalanceSetAtUtc = null, string? OpeningBalanceSetBy = null);

/// <summary>
/// The company admin's part of the store profile. FiscalIds keys must exist in the country
/// profile; currency and tax rules are operator-owned and not accepted here.
/// </summary>
public record UpsertStoreRequest(
    string NameAr, string NameFr, string NameEn, string Address, string Phone,
    IReadOnlyDictionary<string, string>? FiscalIds, string ReceiptFooter,
    bool RequireCustomerEmail, bool RequireCustomerBirthDate, bool RequireCustomerPhone,
    bool RequireCustomerAddress, bool RequireCustomerTaxId,
    // Null keeps the current value, so older clients do not reset the programme.
    bool? LoyaltyEnabled = null, decimal? LoyaltyPointsPerUnit = null, decimal? LoyaltyPointValue = null,
    int? LoyaltyMinRedeemPoints = null,
    // Absent: unchanged. Explicitly null: back to the country profile's face. A face key: that
    // face, if it is one of the bundled ones.
    Optional<string?> UiFont = default,
    string? AddressAr = null, string? AddressEn = null,
    string? ReceiptFooterAr = null, string? ReceiptFooterEn = null);

// ---------- printed documents (docs/07 §4.3) ----------

/// <summary>
/// The kinds a company may attach a printed text to. Keys of <c>StoreDocumentsDto.DocumentTexts</c>;
/// anything else in a PUT is refused rather than stored, so a typo cannot create a ninth kind.
/// </summary>
public static class DocumentTextKinds
{
    public const string Invoice = "invoice";
    public const string CreditNote = "creditNote";
    public const string ReceiptVoucher = "receiptVoucher";
    public const string Statement = "statement";
    public const string RentalContract = "rentalContract";
    public const string IssueNote = "issueNote";
    public const string ReturnNote = "returnNote";
    public const string Payslip = "payslip";
    public const string PaymentVoucher = "paymentVoucher";

    public static readonly IReadOnlyList<string> All =
        new[] { Invoice, CreditNote, ReceiptVoucher, Statement, RentalContract, IssueNote, ReturnNote, Payslip, PaymentVoucher };

    /// <summary>
    /// The start of every named rental-contract wording's key (PinSay #302): <c>rentalContract.</c>
    /// and up to 16 lower-case letters or digits, chosen by the screen that adds it. The text is kept
    /// under that key beside the kinds above, so the yearly allowance and the history treat it as a
    /// kind of its own.
    /// </summary>
    public const string ContractWordingPrefix = RentalContract + ".";

    /// <summary>A company has at most this many named contract wordings besides the standard one.</summary>
    public const int MaxContractWordings = 20;

    /// <summary>A wording's name is a label on a select, not a title.</summary>
    public const int MaxContractWordingName = 80;

    /// <summary>Whether <paramref name="key"/> has the shape of a named contract wording's key.</summary>
    public static bool IsContractWording(string? key) =>
        key is not null && key.Length > ContractWordingPrefix.Length && key.Length <= ContractWordingPrefix.Length + 16
        && key.StartsWith(ContractWordingPrefix, StringComparison.Ordinal)
        && key[ContractWordingPrefix.Length..].All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9');
}

/// <summary>
/// One named rental-contract wording (PinSay #302): its key, and its name per language for the
/// contract form's select. Its text is <c>StoreDocumentsDto.DocumentTexts[Key]</c>.
/// </summary>
public record ContractWordingDto(string Key, IReadOnlyDictionary<string, string> Names);

/// <summary>
/// The company's printed-document settings — <c>GET/PUT /api/catalog/store/documents</c>, Admin.
/// The same record both ways: what the screen reads is exactly what it writes back.
///
/// <para>
/// <b>Not part of <see cref="StoreDto"/> and not part of <see cref="UpsertStoreRequest"/></b>,
/// so a till never pulls it, and the PUT writes the store row with a plain SaveChanges so its
/// <c>Version</c> does not move for fields the replica does not carry.
/// </para>
/// </summary>
/// <param name="DocumentTexts">
/// <c>{ [kind]: { [lang]: text } }</c>, kinds from <see cref="DocumentTextKinds"/>, languages
/// as the country profile enables them, at most <c>Store.DocumentTextMaxLength</c> (4,000)
/// characters per text. Empty when nothing is configured; the frame prints the text through
/// the usual localized fallback chain and prints nothing for an empty one. The code writes no
/// legal terms — the owner pastes his own.
/// </param>
/// <param name="BankDetails">Free text: RIB/IBAN/BIC labelling differs by country.</param>
/// <param name="ChangesPerYear">
/// How many times each kind's wording may be rewritten in one calendar year — the admin's own
/// setting, three by default (<c>Store.DefaultDocumentTextChangesPerYear</c>). Sent on the GET
/// and honoured on the PUT, so the screen that shows the allowance is the screen that sets it.
/// </param>
/// <param name="Allowances">
/// Read-only on the way back: what is left of this year's allowance per kind. Ignored on a PUT —
/// the server counts, the screen does not get to tell it what it has used.
/// </param>
/// <param name="LogoUrl">
/// The link to the company's logo, printed in every document header (signed-in only,
/// <c>StoreLogo.StoreUrl</c>); null = no logo. On the PUT: that link keeps it, a <c>data:</c> URL
/// replaces it (capped in size), null or empty removes it.
/// </param>
/// <param name="TitleLanguages">
/// The languages every document title prints in, in order; empty or null = the document
/// language alone. Codes the country profile can enable, at most three, no repeats.
/// </param>
/// <param name="ItemNameLanguages">
/// The languages an item's name prints in on the rental papers, first as the name and a second
/// beneath; empty or null = the French name signed, with the Arabic beneath. Same rules.
/// </param>
/// <param name="ContractWordings">
/// The named rental-contract wordings beside the standard one, in the order the contract form
/// lists them (PinSay #302). Each must have a text in <paramref name="DocumentTexts"/> under its
/// key. On the PUT the list is the whole set: a wording left out is removed, which is refused
/// while any contract prints it. Null on the PUT leaves them as they are, so an older screen that
/// does not know the field cannot wipe them.
/// </param>
public record StoreDocumentsDto(
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> DocumentTexts,
    string? Email = null, string? Website = null, string? BankDetails = null,
    int ChangesPerYear = 3,
    IReadOnlyList<DocumentTextAllowanceDto>? Allowances = null,
    string? LogoUrl = null,
    IReadOnlyList<string>? TitleLanguages = null,
    IReadOnlyList<string>? ItemNameLanguages = null,
    IReadOnlyList<ContractWordingDto>? ContractWordings = null);

/// <summary>
/// What is left of one document kind's yearly rewriting allowance.
///
/// <para>
/// The year is the calendar year <b>in the company's time zone</b>, never UTC, and each kind is
/// counted on its own — rewriting the invoice's payment terms does not spend the rental
/// contract's allowance. A save that changed nothing was not counted.
/// </para>
/// </summary>
/// <param name="ResetsOnUtc">
/// The instant the count goes back to zero: the company's own 1 January, expressed in UTC so the
/// screen can say the date without doing time-zone arithmetic of its own.
/// </param>
public record DocumentTextAllowanceDto(
    string Kind, int Year, int Limit, int Used, int Remaining, DateTime ResetsOnUtc);

/// <summary>
/// One superseded wording, newest first — <c>GET /api/catalog/store/documents/history</c>, Admin.
/// <see cref="PreviousTexts"/> is the wording as it stood <b>before</b> that change, per language,
/// which is what makes the copy a customer signed under readable years later.
/// </summary>
public record DocumentTextChangeDto(
    Guid Id, string Kind, int ChangeYear, DateTime ChangedAtUtc,
    IReadOnlyDictionary<string, string> PreviousTexts, string? ChangedBy = null);

// ---------- gift cards ----------

/// <summary>
/// A card sold for cash at <paramref name="BranchId"/> (owner, 2026-11-01, SAL-17): the money goes into that
/// branch's box and is owed to the customer until spent. A non-admin's branch is their own; none means the main branch.
/// </summary>
public partial record IssueGiftCardRequest(string? Code, decimal Amount, DateTime? ExpiresAtUtc, Guid? CustomerId, Guid? BranchId = null);
public record UpdateGiftCardRequest(bool IsActive, DateTime? ExpiresAtUtc, Guid? CustomerId);
/// <summary>
/// A hand change to a card's balance, of one <paramref name="Kind"/> (owner, 2026-11-01, SAL-17):
/// <c>topUp</c> — paid in cash at <paramref name="BranchId"/>, <paramref name="Delta"/> positive;
/// <c>cashOut</c> — part of the balance handed back in cash there, <paramref name="Delta"/> negative;
/// <c>correction</c> (or none, as before) — a mistake put right, no money and nothing booked.
/// </summary>
public partial record AdjustGiftCardRequest(decimal Delta, string Reason, string? Kind = null, Guid? BranchId = null);

// ---------- branches & stock ----------

public record UpsertBranchRequest(
    string NameAr, string NameFr, string NameEn, string Address, string Phone,
    bool IsMain, bool AllowNegativeStock, bool IsActive,
    string? AddressAr = null, string? AddressEn = null,
    // The branch's own fiscal identifiers, keyed and checked like the store's; a blank one prints
    // the company's. Null leaves the stored ones as they are (a client that predates the field),
    // an empty object clears them.
    IReadOnlyDictionary<string, string>? FiscalIds = null,
    // The branch's logo as the branches tab shows it: the link it was given keeps it, a data URL
    // replaces it (StoreLogo, capped at BranchMaxBytes), null or empty removes it.
    string? LogoUrl = null,
    // The kind: a warehouse holds stock and never sells. IsMain makes it the main one of its kind.
    bool IsWarehouse = false);

/// <summary>One product's stock at one branch, with the branch name resolved for display.</summary>
public record ProductStockDto(Guid ProductId, Guid BranchId, string BranchName, decimal StockQty, decimal ReorderLevel);

/// <summary>One article's available quantity at one branch: the rental form's item search.</summary>
public record StockLevelDto(Guid ProductId, decimal StockQty);

public record LowStockDto(
    Guid ProductId, string Sku, string NameFr, Guid BranchId, string BranchName,
    decimal StockQty, decimal ReorderLevel);

/// <summary>
/// QtyDelta moves stock at BranchId (receiving, breakage, stocktake correction — sign carries
/// the direction). ReorderLevel, when provided, is set regardless of QtyDelta, so an admin can
/// tune a threshold without also moving stock.
/// </summary>
public partial record StockAdjustRequest(Guid BranchId, decimal QtyDelta, string Reason, decimal? ReorderLevel);

// ---------- devices (till registration) ----------

/// <param name="OnWarehouse">
/// The till sits on a warehouse, which does not sell (the owner, 2026-10-05). Only a till that
/// was there before the rule can be, until the next start moves it to the main branch; the list warns.
/// </param>
public record DeviceAdminDto(
    Guid Id, string Code, string Name, Guid BranchId, string BranchName, bool IsActive,
    DateTime? LastSyncAtUtc, long LastCursor, bool OnWarehouse = false);

public partial record UpsertDeviceRequest(string Code, string Name, Guid BranchId, bool IsActive);

// ---------- users (back office) ----------

/// <summary>
/// Back-office view of a user. Never carries PasswordHash or PinHash — those never leave
/// the server except as a BCrypt hash inside the sync payload for cashiers only.
/// </summary>
public record UserAdminDto(
    Guid Id, string Username, string FullName, string Role, bool IsActive,
    long Version, bool IsDeleted,
    // What this user holds beyond their role, and what an administrator holds by virtue of it.
    // Two lists rather than one: a screen has to show that an admin's grant is inherited and
    // cannot be taken away here, which a single merged list cannot express.
    IReadOnlyList<string>? Permissions = null,
    IReadOnlyList<string>? EffectivePermissions = null,
    // The branch this user works at (BranchLock). Null only on a legacy row: that user works at
    // the main branch, and the users list says "no branch assigned" until one is chosen.
    Guid? BranchId = null,
    // The cashier's own till (Infrastructure/Auth/CashierTills): its code, null for a user with
    // none, and whether it is switched on.
    string? TillCode = null,
    bool TillActive = false,
    // The sign-in number the server drew when the account was created (8 digits). Null on an account
    // that predates Login IDs: it signs in with its username, shown as such.
    string? LoginId = null,
    // The employee the account belongs to, and their HR number (EMP-000001) — a separate identifier
    // from the Login ID. Null on the bootstrap administrator and on accounts not linked yet.
    Guid? EmployeeId = null,
    string? EmployeeNumber = null,
    // "Sell untaxed at the till" (sales.untaxed; owner, 2026-11-02): whether they hold it, and whether it is
    // their role's — then the users screen shows it ticked and locked — rather than their own tick.
    bool SellsUntaxed = false,
    bool SellsUntaxedByRole = false,
    // The role that decides their access (AUTH-1): its id, its name and its scope. Role above is its legacy
    // projection (Admin, Manager or Cashier, from the scope), which a role the company created also has.
    Guid? RoleId = null,
    string? RoleName = null,
    string? RoleScope = null,
    // Every key given to this account on top of its role, whatever the module (AUTH-1 §17.10), in the
    // catalogue's order; and the version of its role and grants, which the users screen sends back so a save
    // made on an older view is refused rather than taking away what it never saw.
    IReadOnlyList<string>? AdditionalPermissions = null,
    long AuthorizationVersion = 0,
    // Two-step login is the user's own choice; the users screen shows it and may only turn it off.
    bool TwoFactorOn = false);

/// <param name="Permissions">
/// Named permissions granted on top of the role. Empty for almost everyone — see
/// <c>ParadisERP.Domain.Permissions</c> for why the concept exists at all. Unknown names are
/// dropped rather than stored, so a typo grants nothing.
/// </param>
/// <param name="BranchId">
/// The branch this user works at — <b>required</b>, whatever the role (BranchLock, 2026-09-30):
/// 400 in words without one. For a cashier it is also the terminal binding of docs/07 §2.2.
/// <para>Defaults to the employee's own branch when absent.</para>
/// </param>
/// <param name="EmployeeId">
/// The employee the account is for — <b>required</b> (owner, 2026-10-05): 400 <c>employee_required</c>
/// without one. The account's name is the employee's, and the server draws its Login ID; a
/// <paramref name="Username"/> or <paramref name="FullName"/> in the body is ignored.
/// </param>
/// <param name="SellsUntaxed">
/// "Sell untaxed at the till" on the account itself (sales.untaxed; owner, 2026-11-02). Absent or false:
/// a new cashier starts without it. Stored nowhere for a role that already gives it.
/// </param>
/// <param name="RoleId">
/// The role, by id — a system role or one the company created (AUTH-1 Step 5). When sent, <paramref name="Role"/>
/// is ignored; absent, <paramref name="Role"/> names a system role as before.
/// </param>
/// <param name="AdditionalPermissions">
/// Every key given to the account on top of its role, from the whole catalogue (AUTH-1 §17.10). When sent, it
/// replaces <paramref name="Permissions"/> and <paramref name="SellsUntaxed"/>, which are then ignored. An unknown
/// key is refused (<c>unknown_permission</c>), and so is a key the caller does not hold (<c>grant_beyond_own</c>).
/// </param>
public record CreateUserRequest(
    string? Username, string? FullName, string Role, string Password, string? Pin,
    IReadOnlyList<string>? Permissions = null,
    Guid? BranchId = null,
    Guid? EmployeeId = null,
    bool? SellsUntaxed = null,
    Guid? RoleId = null,
    IReadOnlyList<string>? AdditionalPermissions = null);

/// <summary>Links an account that predates the rule to its employee. Its sign-in name is unchanged.</summary>
public record LinkUserEmployeeRequest(Guid EmployeeId);

/// <param name="BranchId">
/// <b>Absent</b> from the body leaves the branch as it is, so a client that predates it cannot
/// move a user by not knowing the field; a <b>guid</b> moves the user; an explicit <b>null</b> is
/// refused (400) — a user's branch may be changed, never removed (BranchLock). The users tab
/// always sends it.
/// </param>
/// <param name="SellsUntaxed">
/// "Sell untaxed at the till" on the account itself. <b>Absent</b> leaves it as it is, so a client that
/// predates it cannot take it away by not knowing the field.
/// </param>
/// <param name="RoleId">The role, by id (AUTH-1 Step 5). When sent, <paramref name="Role"/> is ignored.</param>
/// <param name="AdditionalPermissions">
/// The account's whole set of keys on top of its role (AUTH-1 §17.10): what is not in it is taken away. When
/// sent, <paramref name="Permissions"/> and <paramref name="SellsUntaxed"/> are ignored. <b>Absent</b> leaves the
/// grants as the older fields say, so a client that predates it changes nothing it does not know.
/// </param>
/// <param name="AuthorizationVersion">
/// The account's <c>authorizationVersion</c> as the screen opened it. When sent and no longer current — its role
/// or grants changed meanwhile — the save is refused (409 <c>user_changed</c>) and nothing changes.
/// </param>
public record UpdateUserRequest(
    string FullName, string Role, bool IsActive,
    IReadOnlyList<string>? Permissions = null,
    Optional<Guid?> BranchId = default,
    bool? SellsUntaxed = null,
    Guid? RoleId = null,
    IReadOnlyList<string>? AdditionalPermissions = null,
    long? AuthorizationVersion = null);

public record SetCredentialRequest(string Value);

// ---------- roles and permissions (AUTH-1 Step 5, REDESIGN_PLAN.md §17.8) ----------

/// <summary>A module of the catalogue and its keys, in the catalogue's order. The screens name each key <c>perm.&lt;key&gt;</c>.</summary>
public record PermissionModuleDto(string Module, IReadOnlyList<string> Keys);

/// <summary>
/// One role as the roles screen lists it. <paramref name="Members"/> counts the accounts on it that were not
/// removed — active or not — whose access a save changes. What may be changed is said here, so a screen
/// never infers it from a name: the Admin role nothing; the Manager and Cashier roles their permissions;
/// a role the company created everything, and it may be deleted once no account, removed ones included,
/// names it.
/// </summary>
public record RoleDto(
    Guid Id, string? Key, string Name, string? Description, bool IsActive, bool IsSystem, string Scope, long Version,
    IReadOnlyList<string> Permissions, int Members, bool PermissionsEditable, bool DetailsEditable, bool Deletable);

/// <summary>An account on a role.</summary>
public record RoleMemberDto(Guid Id, string FullName, string SignIn, Guid? BranchId, bool IsActive);

/// <summary>
/// Creates or edits a role. On an edit, an absent field is left as it is. <paramref name="Scope"/> is
/// <c>AssignedBranch</c> (the default), <c>TillBranch</c> or <c>Company</c>. <paramref name="Version"/>, when
/// sent, is the version the screen opened; a role changed since is refused (409 <c>role_changed</c>).
/// </summary>
public record UpsertRoleRequest(
    string? Name = null, string? Description = null, string? Scope = null, bool? IsActive = null,
    IReadOnlyList<string>? Permissions = null, long? Version = null);

// ---------- sales (back-office reads, branch-scoped for a cashier — docs/07 §2.3) ----------

// These three replace the anonymous projections GET /api/sales and GET /api/sales/{id} used
// to return, property for property, so the wire is unchanged and the query bodies can move
// into SalesQueries taking a Guid? scope and be tested without HTTP. Flat shapes rather than
// entities for the reason the endpoint always gave: EF fixup fills SaleLine.Sale even under
// AsNoTracking and the cycle would throw mid-response.

public record SaleRowDto(
    Guid Id, string Reference, string DeviceCode, Guid BranchId, DateTime SoldAtUtc, DateTime ReceivedAtUtc,
    string CashierName, Guid? CustomerId,
    decimal SubTotalHt, decimal TaxTotal, decimal DiscountTotal, decimal GrandTotalTtc,
    decimal RoundingAdjustment, decimal PaidAmount, decimal ChangeAmount,
    string PaymentMethod, string Status, bool StockWarning, int LineCount);

public record SaleLineDto(
    Guid Id, Guid ProductId, string Sku, string NameSnapshot, decimal Qty, decimal UnitPrice,
    decimal TaxRate, decimal LineDiscount, decimal LineTotalTtc, decimal LineTaxAmount);

public record SalePaymentDto(Guid Id, string Method, decimal Amount, string? Reference);

public record SaleDetailDto(
    Guid Id, string Reference, string DeviceCode, Guid BranchId, long LocalSeq,
    DateTime SoldAtUtc, DateTime ReceivedAtUtc,
    Guid? CustomerId, Guid CashierUserId, string CashierName,
    decimal SubTotalHt, decimal TaxTotal, decimal DiscountTotal, decimal GrandTotalTtc,
    decimal RoundingAdjustment, decimal PaidAmount, decimal ChangeAmount,
    string PaymentMethod, string Status,
    Guid? RefundOfSaleId, string? Note, bool StockWarning,
    IReadOnlyList<SaleLineDto> Lines, IReadOnlyList<SalePaymentDto> Payments);

// ---------- reports ----------

// Refunds are subtracted from every figure below and also reported on their own, so the
// tax breakdown can be copied straight onto a declaration.
public record SalesSummaryDto(
    DateTime FromUtc, DateTime ToUtc, int SaleCount, int RefundCount,
    decimal GrossTtc, decimal NetHt, decimal TaxTotal, decimal DiscountTotal,
    decimal RefundTotal,
    IReadOnlyList<TaxBucketDto> TaxBreakdown,
    IReadOnlyList<TopProductDto> TopProducts,
    // Populated only when the request was not itself scoped to one branch, so a manager
    // looking at the whole company gets a per-branch comparison for free.
    IReadOnlyList<BranchSummaryDto> BranchBreakdown,
    // Net cash rounding given (negative) or collected (positive), outside the tax figures.
    decimal RoundingTotal = 0,
    // Net amounts settled with gift cards and loyalty points (refunds subtracted).
    decimal GiftCardTotal = 0,
    decimal LoyaltyTotal = 0);

public record TaxBucketDto(decimal Rate, decimal BaseHt, decimal TaxAmount);

/// <summary>One close or reopen of the books (CLO-01), as the audit trail shows it.</summary>
public record BookClosingDto(Guid Id, DateOnly? ClosedThrough, bool IsReopen, string? Reason, string? UserName, DateTime AtUtc);

/// <summary>The books now — closed through a day, or nothing closed — and the trail of every close and reopen, newest first.</summary>
public record BookClosingStateDto(DateOnly? ClosedThrough, IReadOnlyList<BookClosingDto> History);

/// <summary>Close the books through <paramref name="Through"/>, the last day of a month that has ended.</summary>
public record CloseBooksRequest(DateOnly Through, string? Note);

/// <summary>Reopen the books back to <paramref name="Through"/> (an earlier month end), or every month when null. A reason is required.</summary>
public record ReopenBooksRequest(DateOnly? Through, string Reason);

/// <summary>
/// Sales whose margin is incomplete because their goods left stock with no cost (SAL-02), for a
/// period of days on the company's clock. Refunds subtract. <paramref name="SalesHt"/> is every
/// sale of the period before tax, so the share left uncosted can be read against it;
/// <paramref name="ProductsWithoutCost"/> counts the active stock-tracked products whose cost is
/// still 0 today, sold or not.
/// </summary>
public record MissingCostReportDto(
    DateOnly From, DateOnly To, decimal SalesHt, decimal UncostedHt, int UncostedSales,
    int ProductsWithoutCost, IReadOnlyList<MissingCostRowDto> Rows);

/// <summary>
/// One product sold with no cost in the period. <paramref name="CostNow"/> is its cost today:
/// 0 while it is still missing; once set, <paramref name="ValueAtCostNow"/> is what the
/// quantity sold would have cost at it — an estimate for the owner, never posted.
/// </summary>
public record MissingCostRowDto(
    Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn,
    decimal Qty, decimal RevenueHt, int Sales, DateTime LastSoldAtUtc, decimal CostNow, decimal? ValueAtCostNow);
public record TopProductDto(Guid ProductId, string Name, decimal Qty, decimal Revenue);
public record BranchSummaryDto(Guid BranchId, string BranchName, int SaleCount, decimal GrossTtc);

// ---------- stock ----------

/// <summary>
/// Goods entering the company from a supplier, at the branch that receives them (owner,
/// 2026-10-12: "a branch can buy items from suppliers itself"). Until then receiving always landed
/// at the main warehouse and had no branch at all.
/// </summary>
/// <param name="SupplierId">
/// Who delivered (owner, 2026-10-10: a basic suppliers module): the note's value goes on that
/// supplier's account. Optional — a note from before suppliers existed, or one nobody names, posts
/// to 401 with no supplier, exactly as before.
/// </param>
/// <param name="BranchId">
/// Where the goods enter. Missing, the main warehouse, as every note before 2026-10-12 did. Honoured
/// for the Admin only: for anyone else the branch lock replaces it with their own branch
/// (<see cref="IRaisedAtBranch"/>), so a branch user always receives into their branch.
/// </param>
public partial record ReceivingRequest(IReadOnlyList<ReceivingLine> Lines, string? Reference, string? Note, Guid? SupplierId = null,
    Guid? BranchId = null);

/// <summary>
/// <paramref name="UnitCost"/> and <paramref name="AssetUnitValue"/> are optional: a repeat
/// delivery of something already priced need not restate them. When given they update the
/// product, because receiving is the only moment either figure is actually known.
/// </summary>
public record ReceivingLine(Guid ProductId, decimal Qty, decimal? UnitCost, decimal? AssetUnitValue);

/// <summary>
/// What a branch already held on the day the company adopted the system. Accepted only until
/// the branch is declared open for operation.
/// </summary>
public partial record OpeningBalanceRequest(Guid BranchId, IReadOnlyList<OpeningBalanceLine> Lines, string? Note);

/// <summary>
/// <paramref name="UnitValue"/> is what the branch's existing stock is carried at. Design §5.3
/// says an opening balance states quantity <b>and</b> value; without it there is no figure to
/// put against opening-balance equity and the ledger has to invent one. Omitted, the product's
/// cost price is used.
/// </summary>
public record OpeningBalanceLine(Guid ProductId, decimal Qty, decimal? UnitValue = null);

/// <summary>
/// A branch's stock sheet as read, before anything is written: the columns it offers as
/// quantities, the one its header names as the available count (null when none does), and per
/// line what it would become. <see cref="OpeningImportRowDto.Match"/> is <c>existing</c>,
/// <c>new</c> or <c>ambiguous</c> (several products answer to that name).
/// </summary>
public record OpeningImportPreviewDto(
    string FileName, string SheetName, IReadOnlyList<string> SheetNames,
    int HeaderRow, string NameColumn, string NameHeader,
    IReadOnlyList<OpeningImportColumnDto> Columns, int? QuantityColumn,
    IReadOnlyList<OpeningImportRowDto> Rows);

public record OpeningImportColumnDto(int Index, string Letter, string Header);

/// <param name="Values">One per entry of <see cref="OpeningImportPreviewDto.Columns"/>, null where the cell is not a number.</param>
/// <param name="DuplicateOfRow">The earlier sheet row naming the same article, when there is one.</param>
public record OpeningImportRowDto(
    int Row, string Name, IReadOnlyList<decimal?> Values, string Match,
    Guid? ProductId, string? Sku, string? ProductNameAr, string? ProductNameFr, string? ProductNameEn,
    int? DuplicateOfRow);

/// <summary>
/// Loading a branch's stock sheet: every line becomes that branch's opening quantity, and a
/// line naming no existing article creates it first. One transaction — a sheet half-loaded
/// would leave products with no count and a count that matches no paper.
/// </summary>
/// <param name="NewProductsRentable">Whether the articles this creates are hired out rather than sold.</param>
/// <param name="SkuPrefix">What the codes given to new articles start with; <c>ART-</c> when blank.</param>
public partial record OpeningImportRequest(
    Guid BranchId, IReadOnlyList<OpeningImportLine> Lines, string? Note,
    bool NewProductsRentable = true, string? SkuPrefix = null);

/// <param name="ProductId">The article chosen on the preview; null to match by name, or create it.</param>
public record OpeningImportLine(string Name, decimal Qty, Guid? ProductId = null);

public record OpeningImportResultDto(
    Guid BranchId, Guid DocumentId, int Lines, int Matched, int Created, decimal Value,
    IReadOnlyList<OpeningImportCreatedDto> CreatedProducts);

public record OpeningImportCreatedDto(Guid Id, string Sku, string NameAr);

/// <summary>Moving a quantity between the states of section 3.2 — for example available to maintenance.</summary>
public partial record StockStateChangeRequest(Guid ProductId, Guid BranchId, decimal Qty, string From, string To, string? Reason);

/// <summary>
/// One branch's stock of one product by state. For a caller other than the Admin, another branch's
/// row carries <see cref="Available"/> only — the other states come back null: the owner allows
/// looking up availability elsewhere ("if he has been given the permission"), and nothing more of
/// another branch. The caller's own branch is always complete.
/// </summary>
public record BranchAvailabilityDto(
    Guid BranchId, string NameAr, string NameFr, string NameEn, bool IsMain,
    decimal Available, decimal? OnRent, decimal? Maintenance, decimal? Scrap, decimal? Owned,
    bool IsWarehouse = false);

// ---------- ledger ----------

public record LedgerAccountDto(
    Guid Id, string Code, string NameAr, string NameFr, string NameEn,
    string Type, bool IsSystem, bool IsActive, int SortOrder);

/// <summary>
/// An account the admin adds to extend the seeded chart. The type cannot be changed once
/// rows exist against it, and a system account's code and type can never change at all:
/// modules post to them by code and would otherwise land on the wrong side of the balance
/// sheet without anything failing.
/// </summary>
public record UpsertLedgerAccountRequest(
    string Code, string NameAr, string NameFr, string NameEn, string Type, bool IsActive, int SortOrder);

/// <summary>
/// One line of a journal entry. <see cref="Folded"/> marks the line a branch holding only part of an
/// inter-branch entry reads in place of the other branches' lines: their totals, and nothing else.
/// </summary>
public record LedgerEntryLineDto(
    Guid Id, Guid AccountId, string AccountCode, string AccountNameFr, string AccountType,
    Guid BranchId, decimal Debit, decimal Credit, string? Memo, bool Folded = false);

public record LedgerEntryDto(
    Guid Id, DateTime EntryDate, DateTime CreatedAtUtc, Guid BranchId, string? BranchName,
    string SourceType, Guid SourceId, string? Description,
    bool IsReversal, Guid? ReversalOfEntryId, bool IsInterBranch, Guid? CounterpartBranchId,
    string? CreatedBy, decimal Total, IReadOnlyList<LedgerEntryLineDto> Lines,
    // On a reversal, the date of the entry it corrects: a mistake in a closed month is corrected in the current
    // month, naming the original (COR-E01, owner 2026-11-05), so the screen says which day and month it was.
    DateTime? ReversalOfEntryDate = null);

/// <summary>Why an entry is being cancelled. A reversal without a reason is an unexplained figure.</summary>
public record ReverseEntryRequest(string Reason);

public record TrialBalanceRowDto(
    Guid AccountId, string Code, string NameAr, string NameFr, string NameEn, string Type,
    decimal Debit, decimal Credit, decimal Balance);

public record TrialBalanceDto(
    DateTime From, DateTime To, Guid? BranchId, bool Consolidated,
    IReadOnlyList<TrialBalanceRowDto> Rows, decimal TotalDebit, decimal TotalCredit, bool Balanced);

public record ProfitAndLossLineDto(string Code, string NameAr, string NameFr, string NameEn, decimal Amount);

public record BranchResultDto(Guid BranchId, string BranchName, decimal Income, decimal Expenses, decimal NetProfit);

/// <summary>
/// <paramref name="EliminatedInterBranch"/> is the profit that would have been reported had
/// movements between the company's own branches been counted. Consolidating is not adding the
/// branches up, and this is the figure that says by how much.
/// </summary>
public record ProfitAndLossDto(
    DateTime From, DateTime To, Guid? BranchId, bool Consolidated,
    IReadOnlyList<ProfitAndLossLineDto> Income, IReadOnlyList<ProfitAndLossLineDto> Expenses,
    decimal TotalIncome, decimal TotalExpenses, decimal NetProfit,
    IReadOnlyList<BranchResultDto> ByBranch, decimal EliminatedInterBranch);

public record BalanceSheetLineDto(string Code, string NameAr, string NameFr, string NameEn, decimal Amount);

public record BalanceSheetDto(
    DateTime AsOf, Guid? BranchId, bool Consolidated,
    IReadOnlyList<BalanceSheetLineDto> Assets,
    IReadOnlyList<BalanceSheetLineDto> Liabilities,
    IReadOnlyList<BalanceSheetLineDto> Equity,
    decimal TotalAssets, decimal TotalLiabilities, decimal TotalEquity, decimal NetResult, bool Balanced);

public record AccountStatementRowDto(
    Guid EntryId, DateTime EntryDate, Guid BranchId, string SourceType, Guid SourceId,
    string? Description, bool IsReversal, decimal Debit, decimal Credit, decimal RunningBalance);

public record AccountStatementDto(
    Guid AccountId, string Code, string NameAr, string NameFr, string NameEn, string Type,
    DateTime From, DateTime To, Guid? BranchId,
    decimal Opening, decimal Closing, IReadOnlyList<AccountStatementRowDto> Rows);

// ---------- renting ----------

/// <summary>
/// The two company-level rental settings (§5.5, §5.6). They live on the store row beside the
/// loyalty programme, and are deliberately absent from <see cref="StoreDto"/>: renting does not
/// touch the sync engine (§5.9), and a till has no use for an overdue multiplier it can never
/// apply.
/// </summary>
/// <param name="DayCutoffHour">
/// The local hour (0–23) after which a rental day is spent, or null for today's rule (the delivery
/// day counts, the return day never does) — see <c>Store.RentalDayCutoffHour</c>.
/// </param>
/// <param name="MinimumDays">The minimum hire in days, 0 for none (<c>Store.RentalMinimumDays</c>).</param>
public record RentalSettingsDto(decimal OverdueMultiplier, bool DepositEnabled, decimal TaxRate = 0,
    int? DayCutoffHour = null, int MinimumDays = 0);

/// <param name="TaxRate">
/// Percentage rental documents are taxed at. 0 means hire is not taxed here, which is the
/// default — whether it is taxed depends on the business and its jurisdiction, and a rate
/// inferred by the software is the hardcoded tax rule the country profile exists to prevent.
/// </param>
public record UpdateRentalSettingsRequest(decimal OverdueMultiplier, bool DepositEnabled, decimal TaxRate = 0,
    int? DayCutoffHour = null, int MinimumDays = 0);

/// <summary>
/// One item's predefined rental price and its own overdue multiplier. Null leaves the rental
/// price unchanged, while a null multiplier clears that override and puts the item back on the
/// company figure. This is its own route so a catalog client which knows nothing about renting
/// cannot clear either value by omission.
/// </summary>
public record SetProductOverdueMultiplierRequest(decimal? OverdueMultiplier, decimal? RentalUnitPrice = null,
    // The fixed value charged per unit not returned or wrecked, before tax; the contract line
    // starts from it. Null clears it, like the multiplier: the settings screen always sends it.
    decimal? RentalDamageUnitPrice = null);

/// <summary>
/// One item's own overdue multiplier, read back (docs/07 §3.2): the answer of
/// <c>GET /settings/products</c>, <c>GET /settings/products/{id}</c> and the PUT alike.
/// <b>Never a member of <see cref="ProductDto"/></b> — that is the sync payload to every till,
/// and renting does not touch the sync engine. <paramref name="OverdueMultiplier"/> is null
/// when the item has no override and is on the company figure.
/// </summary>
public record ProductOverdueMultiplierDto(
    Guid ProductId, string Sku, string NameAr, string NameFr, string NameEn,
    decimal? OverdueMultiplier, decimal RentalUnitPrice = 0,
    decimal? RentalDamageUnitPrice = null);

/// <summary>
/// <paramref name="ParentLineIndex"/> is the position in this request's own line list, because
/// the accessory's parent has no id until the contract is saved.
/// <para>
/// <paramref name="OverdueUnitPrice"/> is normally left out and derived from the rental price
/// and the multiplier in force at signing; supply it only when the figure was negotiated.
/// Either way it is frozen on the line, because it has to be signed with the others or there
/// is nothing to stand on when the customer refuses the difference.
/// </para>
/// </summary>
public record RentalContractLineRequest(
    Guid ProductId, decimal Qty, int DurationDays,
    decimal RentUnitPrice, decimal DamageUnitPrice,
    decimal? OverdueUnitPrice = null, int? ParentLineIndex = null);

public partial record UpsertRentalContractRequest(
    Guid CustomerId, Guid BranchId, IReadOnlyList<RentalContractLineRequest> Lines,
    DateTime? SignedAtUtc = null, string? Notes = null,
    // Accepted and ignored while the deposit is switched off (§5.6).
    decimal DepositAmount = 0,
    // Who signs. Blank means "prefill for me" from the person or the company's current
    // representative; a name typed here wins, because the actual signer may be a delegate.
    string? SignatoryName = null, string? SignatoryIdNumber = null,
    // The customer's site the goods go to. SiteId names one of the customer's sites; the name
    // and address are snapshotted on the contract, and may be typed when no site row exists.
    Guid? SiteId = null, string? SiteName = null, string? SiteAddress = null,
    // The rest of the signatory's identity as the paper contract records it.
    string? SignatoryIdType = null, string? SignatoryIdIssuer = null,
    DateOnly? SignatoryIdIssuedOn = null, DateOnly? SignatoryBirthDate = null,
    // The lorry named on the contract.
    string? TruckType = null, string? TruckNumber = null, string? TruckOwnerPhone = null,
    // Which named wording the contract prints (PinSay #302): a key of the company's contract
    // wordings, blank for the standard wording, and null to leave it as it is on an edit (an older
    // client that does not send it keeps the contract's).
    string? WordingKey = null,
    // A person customer's contract signed by an agent (owner, 2026-10-06): the signatory fields are
    // then the agent's, as typed. Ignored for a company. False — and every older client — is the
    // customer signing himself.
    bool SignedByAgent = false,
    // "Taxed" or "Untaxed" (owner, 2026-11-02: VAT is optional on every bill). Untaxed freezes 0 on every line and
    // needs finance.invoices.untaxed unless the draft already is. Null: taxed on a new contract, unchanged on an edit,
    // so an older client keeps the contract's.
    string? TaxMode = null);

public record IssueNoteLineRequest(Guid ContractLineId, decimal Qty);

/// <summary>
/// <paramref name="BranchId"/> is the branch the goods physically leave from, which decides
/// whose available balance falls and whose on-rent balance carries them.
/// </summary>
public partial record IssueNoteRequest(
    Guid BranchId, IReadOnlyList<IssueNoteLineRequest> Lines,
    DateTime? IssuedAtUtc = null, string? Note = null,
    string? DriverName = null, string? DriverPhone = null, string? TruckNumber = null,
    // The customer carries the goods himself: no driver or truck is asked, the paper says so.
    bool CustomerTransport = false);

/// <summary>
/// <paramref name="ReportedOnUtc"/> is the day the customer reported the loss or the damage.
/// Rent runs to that day instead of to the settlement — but only on a line where nothing came
/// back, so a loss is declared on its own note.
/// </summary>
public record ReturnNoteLineRequest(
    Guid ContractLineId, decimal QtyOk = 0, decimal QtyPartialDamage = 0,
    decimal MaintenanceValue = 0, decimal QtyTotalDamage = 0, decimal QtyMissing = 0,
    DateTime? ReportedOnUtc = null, string? SameDayUsage = null);

/// <summary>
/// <paramref name="BranchId"/> must be the contract's branch (the owner, 2026-09-30); another is
/// refused (400) and the goods move on afterwards with a stock transfer. Goods an Admin issued
/// from a different branch still record an implicit transfer back to it (§7 decision 2).
/// </summary>
/// <param name="CountReturnDay">
/// Whether the return day is charged. Null takes the default: false under today's rule, and
/// once the company sets a day cutoff hour, true for a return at or after it. A value here is
/// the branch manager's decision and <paramref name="ReturnDayReason"/> says why.
/// </param>
/// <param name="ChargeOverdue">
/// The answer to "charge the late days?" when the goods are back after the end of their period
/// (see <c>RentalLateReturn</c>): true charges them, false waives them and needs
/// <paramref name="LateWaiverReason"/>. Null is the rule as it always was — charged. Ignored on a
/// note that is not late. A decision, never an amount: the server prices every day itself.
/// </param>
/// <param name="LateDaysWaived">
/// With <paramref name="ChargeOverdue"/> true, a deduction of this many late days from each batch
/// (at least one, at most the note's late days). <paramref name="LateAmountWaived"/> is the other
/// kind: this much off the note's late total, before tax (more than nothing, at most that total).
/// One or the other, never both; either needs <paramref name="LateWaiverReason"/>. Both null charges
/// every late day. Ignored, like the rest of the answer, on a note that is not late.
/// </param>
public partial record ReturnNoteRequest(
    Guid BranchId, IReadOnlyList<ReturnNoteLineRequest> Lines,
    DateTime? ReturnedAtUtc = null, string? Note = null,
    string? DriverName = null, string? DriverPhone = null, string? TruckNumber = null,
    bool? CountReturnDay = null, string? ReturnDayReason = null,
    bool CustomerTransport = false,
    bool? ChargeOverdue = null, string? LateWaiverReason = null,
    int? LateDaysWaived = null, decimal? LateAmountWaived = null);

/// <summary>
/// <paramref name="ContractLineId"/> null extends every line, and then
/// <paramref name="UnitPrice"/> is required: one document cannot carry a different price per
/// line, and guessing one line's rate for all of them would misprice the rest silently.
/// </summary>
public record ContractExtensionRequest(
    int ExtraDays, Guid? ContractLineId = null, decimal? UnitPrice = null,
    string? Reason = null, DateTime? RaisedAtUtc = null);

public record RentalContractLineDto(
    Guid Id, Guid ProductId, string Sku, string Name, decimal Qty, int DurationDays,
    decimal RentUnitPrice, decimal DamageUnitPrice, decimal OverdueUnitPrice,
    Guid? ParentLineId,
    // The three numbers §5.5 asks every line to carry, plus what is still with the customer.
    decimal IssuedQty, decimal LeftToIssueQty, decimal SettledQty, decimal StillOutQty,
    // The rate frozen on the line at signing, for the printed contract (docs/07 §4.4). 0 is
    // untaxed hire, which is the default and what every line signed before the rate existed carries.
    decimal TaxRate = 0,
    // The Arabic name snapshotted at signing; null on lines signed before it existed.
    string? NameAr = null,
    decimal RentalTotal = 0m);

/// <summary>
/// One row of <c>GET /api/rentals/contracts</c> (docs/07 §3.1): what is out, with whom, and what
/// it has cost — so the list no longer settles each contract to answer its own question.
///
/// <para>
/// The quantities and money here are <b>what was charged</b>: sums over <c>IssueNoteLines</c>,
/// <c>ReturnNoteLines</c> and the stored <c>RentalCharges</c>, which <c>StoreCharges</c> writes
/// for every engine charge including unposted in-period rent, so <paramref name="ChargeTotal"/>
/// equals the settlement's by construction. The contract screen recomputes from the documents;
/// the two agree today, and a future engine correction would show on the contract before it
/// showed here. <paramref name="StillOutQty"/> is issued − settled, floored at zero, which is the
/// engine's figure because a return note refuses to over-return. <paramref name="OverdueTotal"/>
/// is the charges of kind Overdue only.
/// </para>
/// <para>
/// Filled by three set queries on the listed ids, summed in memory after <c>ToListAsync</c>
/// (SQLite stores decimal as TEXT). The list is capped at 500 ids, well inside MySQL's and the
/// bundled SQLite's parameter limits; a larger <c>Take</c> would need chunking.
/// </para>
/// <para>
/// The first ten members are the projection the route already returned, name for name.
/// <paramref name="DepositAmount"/> is filled only while the deposit switch is on (§5.6).
/// </para>
/// </summary>
public record RentalContractRowDto(
    Guid Id, string Number, Guid CustomerId, string? CustomerName, Guid BranchId,
    string Status, bool IsLocked, DateTime? LockedAtUtc, DateTime SignedAtUtc, DateTime? ClosedAtUtc,
    decimal IssuedQty, decimal SettledQty, decimal StillOutQty,
    decimal ChargeTotal, decimal OverdueTotal,
    decimal? DepositAmount = null,
    // Cancelled before any exit note (owner, 2026-10-06); only rentals.cancelled sees these rows.
    DateTime? CancelledAtUtc = null, string? CancelledBy = null, string? CancelReason = null);

public record RentalContractDto(
    Guid Id, string Number, Guid CustomerId, string? CustomerName, Guid BranchId,
    string Status, bool IsLocked, DateTime? LockedAtUtc, DateTime SignedAtUtc,
    DateTime? ClosedAtUtc, string? Notes, IReadOnlyList<RentalContractLineDto> Lines,
    // Present only while the deposit is switched on; invisible otherwise (§5.6).
    decimal? DepositAmount = null,
    // The signatory snapshot, and whether this contract is held to the rule at all: a contract
    // raised before the rule carries SignatoryRequired false and is named as such on screen.
    string? SignatoryName = null, string? SignatoryIdNumber = null, bool SignatoryRequired = false,
    string CustomerKind = "Unspecified",
    // The site, the rest of the signatory's identity and the lorry, as snapshotted at signing.
    Guid? SiteId = null, string? SiteName = null, string? SiteAddress = null,
    string? SignatoryIdType = null, string? SignatoryIdIssuer = null,
    DateOnly? SignatoryIdIssuedOn = null, DateOnly? SignatoryBirthDate = null,
    string? TruckType = null, string? TruckNumber = null, string? TruckOwnerPhone = null,
    // The files kept with the contract (the signatory's id scan); metadata only, the bytes are
    // fetched by id.
    IReadOnlyList<RentalAttachmentDto>? Attachments = null,
    // Answers 15 and 22: when the goods were all back, what the contract is worth on the
    // customer's account, what confirmed receipts have paid against it, and a short close.
    DateTime? GoodsBackAtUtc = null, decimal Owed = 0m, decimal Paid = 0m,
    string? ClosedShortReason = null, string? ClosedShortBy = null,
    // Answer 8: the contract this one extends, and the one that extended it.
    Guid? ExtendsContractId = null, string? ExtendsContractNumber = null,
    Guid? ExtendedByContractId = null, string? ExtendedByContractNumber = null,
    // The named wording it prints (PinSay #302); null is the standard wording.
    string? WordingKey = null,
    // Signed by the person customer's agent rather than the customer himself (owner, 2026-10-06).
    bool SignedByAgent = false,
    decimal RentalTotal = 0m,
    // Cancelled before any exit note: when, by whom, why (owner, 2026-10-06).
    DateTime? CancelledAtUtc = null, string? CancelledBy = null, string? CancelReason = null,
    // The minimum hire frozen when the contract was raised (Store.RentalMinimumDays); 0 for none.
    int MinimumRentalDays = 0,
    // "Taxed" or "Untaxed": whether its bills carry VAT (owner, 2026-11-02).
    string TaxMode = "Taxed")
{
    public IReadOnlyList<RentalAttachmentDto> Attachments { get; init; } = Attachments ?? Array.Empty<RentalAttachmentDto>();
}

/// <summary>One file kept with a contract — what it is and how big, never the bytes.</summary>
/// <summary>
/// An extension as a new contract (answer 8): the days of the new period, every line carried over
/// at the quantity still out and at the prices it was signed at.
/// </summary>
public record ExtendAsNewContractRequest(int DurationDays, DateTime? SignedAtUtc = null, string? Notes = null);

/// <summary>The Admin closes a paid-short contract; the reason is written down (answer 15).</summary>
public record CloseRentalContractRequest(string? Reason);

/// <summary>Calling a contract off before its exit note; the reason is written down.</summary>
public record CancelRentalContractRequest(string? Reason);

public record RentalAttachmentDto(
    Guid Id, string Kind, string FileName, string ContentType, long Length, DateTime UploadedAtUtc);

public record IssueNoteLineDto(Guid ContractLineId, Guid ProductId, string Sku, decimal Qty);

public record IssueNoteDto(
    Guid Id, string Number, Guid ContractId, Guid BranchId, DateTime IssuedAtUtc,
    string? UserName, string? Note, decimal PeriodRent, IReadOnlyList<IssueNoteLineDto> Lines,
    string? DriverName = null, string? DriverPhone = null, string? TruckNumber = null,
    // The first day of this note's period as the arithmetic counts it: the delivery day, or the
    // next one for goods out at or after the company's day cutoff hour.
    DateOnly? PeriodStartsOn = null,
    bool CustomerTransport = false,
    bool IsCarryOver = false);

public record ReturnNoteLineDto(
    Guid ContractLineId, Guid ProductId, string Sku,
    decimal QtyOk, decimal QtyPartialDamage, decimal MaintenanceValue,
    decimal QtyTotalDamage, decimal QtyMissing, DateTime? ReportedOnUtc,
    string SameDayUsage = "LegacyUnknown", Guid? Id = null,
    IReadOnlyList<ReturnAllocationDto>? Allocations = null);

public record ReturnAllocationDto(Guid Id, Guid IssueNoteId, decimal Qty, string Condition,
    int ChargeableDays, decimal RentalCharge, string SameDayUsage, DateTime IssuedAtUtc);

public record ReturnPreviewRowDto(int ReturnLineIndex, Guid ContractLineId, Guid IssueNoteId,
    string IssueNumber, DateTime IssuedAtUtc, decimal Qty, decimal PhysicalQty,
    bool SameDayUsageRequired, string? SameDayUsage, int? ChargeableDays,
    decimal DailyUnitRate, decimal? RentalCharge);

public record ReturnLatePreviewDto(string DueOn, int Days, decimal Amount, int Minutes);

public record ReturnPreviewDto(IReadOnlyList<ReturnPreviewRowDto> Rows,
    IReadOnlyList<RentalChargeDto> Charges, decimal? ChargeTotal, decimal? UnusedRent,
    decimal CustomerBalance, decimal PreviouslyPaid, decimal? CustomerCredit,
    decimal? ReliefDebtReduction, decimal? ReliefPaymentVoucher, bool IsClosed,
    ReturnLatePreviewDto? Late, bool CanConfirm, IReadOnlyList<string> Problems);

/// <summary>
/// <paramref name="Transfers"/> is what a cross-branch return produced: the quantity that had
/// to move from the branch that issued it to the branch that took it back, so neither ends up
/// holding stock it never received.
/// </summary>
public record ReturnNoteDto(
    Guid Id, string Number, Guid ContractId, Guid BranchId, DateTime ReturnedAtUtc,
    string? UserName, string? Note,
    decimal UnusedRent, decimal ReliefDebtReduction, decimal ReliefPaymentVoucher,
    decimal ChargeTotal, bool ContractClosed,
    IReadOnlyList<ReturnNoteLineDto> Lines,
    IReadOnlyList<RentalChargeDto> Charges,
    IReadOnlyList<ImplicitTransferDto> Transfers,
    string? DriverName = null, string? DriverPhone = null, string? TruckNumber = null,
    // Whether the return day was charged, why, and who decided (see ReturnNoteRequest).
    bool CountReturnDay = false, string? ReturnDayReason = null, string? ReturnDayDecidedBy = null,
    bool CustomerTransport = false,
    bool IsCarryOver = false,
    // Back late: whether the late days were waived, why, and who answered (null: nobody was asked).
    bool WaiveOverdue = false, string? LateWaiverReason = null, string? LateDecidedBy = null,
    // A deduction from the late days: so many days, or so much off the late total it names.
    int? LateDaysWaived = null, decimal? LateAmountWaived = null, decimal? LateAmountFull = null);

public record ImplicitTransferDto(Guid ProductId, Guid FromBranchId, Guid ToBranchId, decimal Qty, string State);

public record RentalChargeDto(
    Guid Id, Guid ContractLineId, string Kind, decimal Amount, Guid ReturnNoteId,
    Guid? IssueNoteId, Guid? ExtensionId, decimal Qty, int Days, decimal UnitPrice, bool IsPosted);

public record ContractExtensionDto(
    Guid Id, string Number, Guid ContractId, Guid? ContractLineId, int ExtraDays,
    decimal UnitPrice, DateTime RaisedOnUtc, DateTime? PeriodEndsOnUtc, bool IsRetroactive,
    string? ApprovedByName, string? Reason);

public record RentalLineSettlementDto(
    Guid LineId, decimal IssuedQty, decimal SettledQty, decimal StillOutQty,
    decimal OverReturnedQty, decimal IssuedPeriodRent, decimal UnusedRent, bool IsCleared,
    IReadOnlyList<RentalChargeDto> Charges);

/// <summary>
/// What a contract owes right now, recomputed from its documents. The stored charges are what
/// the customer was handed; this is the same arithmetic run again over everything raised since.
/// </summary>
public record RentalSettlementDto(
    Guid ContractId, string Status, bool IsClosed, decimal StillOutQty,
    decimal ChargeTotal, decimal UnusedRent,
    decimal Rent, decimal Overdue, decimal Maintenance, decimal Damage, decimal Missing,
    IReadOnlyList<RentalLineSettlementDto> Lines,
    // The deposit (docs/07 §3.3): a figure signed on the contract and held on the customer's
    // behalf — never income, never netted against ChargeTotal or the customer's balance, and
    // outside every figure above. Both null while the deposit switch is off, so a zero never
    // prints. DepositToReturn is the same figure once the contract is closed and 0 before:
    // closing STATES what is owed back and never blocks; no money moves and nothing posts
    // until a payment voucher exists.
    decimal? DepositHeld = null, decimal? DepositToReturn = null);

// ---------- phase 5: invoices, receipts, credit notes and the statement ----------

/// <summary>
/// One line of an invoice typed in the back office. Ignored entirely when the invoice is raised
/// over a sale or a rental note: those lines are copied from the document, never re-entered.
/// </summary>
/// <param name="Days">The days a hire line is billed for; null multiplies by one (InvoiceLine.Days).</param>
public record InvoiceLineRequest(
    Guid? ProductId, string? Sku, string? Name,
    decimal Qty, decimal UnitPrice, decimal TaxRate, decimal Discount = 0, int? Days = null);

/// <summary>
/// Raising an invoice. Exactly one of the three source ids may be given; with none of them the
/// invoice is typed in and <paramref name="Lines"/> is what it bills.
/// </summary>
/// <param name="DueDateUtc">Mandatory: an invoice that can never be overdue is one nobody chases.</param>
/// <param name="CustomerId">Only for a typed invoice; over a source document it comes from the document.</param>
public partial record CreateInvoiceRequest(
    DateTime DueDateUtc,
    Guid? CustomerId = null,
    Guid? BranchId = null,
    DateTime? IssuedAtUtc = null,
    string? TaxMode = null,
    string? Notes = null,
    IReadOnlyList<InvoiceLineRequest>? Lines = null,
    Guid? SaleId = null,
    Guid? IssueNoteId = null,
    Guid? ReturnNoteId = null,
    // How the customer is to pay, printed on the invoice: an active payment method's key, or none.
    string? PaymentMethodKey = null,
    // "full" = paid in full on issue with that payment method; null/"deferred" = stays open, later payments are receipts.
    string? Settlement = null,
    string? SettlementMethodKey = null);

public record InvoiceLineDto(
    Guid Id, Guid? ProductId, string Sku, string Name, decimal Qty, decimal UnitPrice,
    decimal TaxRate, decimal Discount, decimal LineNet, string IncomeAccountCode,
    decimal CreditedQty, int? Days = null,
    // P0.3: the quantity this line took out of stock (its whole Qty when it did, 0 when it took nothing out) and what
    // credit notes have brought back into stock of it since. A credit note may bring back at most the difference.
    decimal StockOutQty = 0, decimal StockReturnedQty = 0);

/// <summary>
/// <paramref name="Prepaid"/> is what was settled on the source document before the invoice
/// existed — never owed and never allocatable. <paramref name="Claimed"/> is what pending
/// vouchers have written against this invoice: spoken for, and deliberately absent from
/// <paramref name="Outstanding"/>, because paper is not payment (§5.4).
/// </summary>
public record InvoiceDto(
    Guid Id, string Number, Guid CustomerId, string? CustomerName, Guid BranchId,
    DateTime IssuedAtUtc, DateTime DueDateUtc, string TaxMode, string Origin,
    string? SourceType, Guid? SourceId, string? Notes,
    decimal SubTotalHt, decimal TaxTotal, decimal GrandTotalTtc,
    decimal Prepaid, decimal Credited, decimal Received, decimal Claimed,
    decimal Outstanding, string Status, int DaysOverdue, string AgeingBucket,
    IReadOnlyList<InvoiceLineDto> Lines,
    // For the printed invoice (docs/07 §4.4). SourceNumber is the paper this one bills — the
    // sale's receipt reference or the rental note's number — null on a direct invoice.
    string? SourceNumber = null,
    // The buyer block frozen at issue (§8 Q12, accepted): what the first print said, so a
    // reprint agrees with it whatever the customer row says today. CustomerName above stays
    // the customer's current name; a printer uses these and falls back to it only when a
    // snapshot is null, which after the backfill means "not recorded", never "unchanged".
    string? BuyerName = null, string? BuyerAddress = null, string? BuyerTaxId = null,
    IReadOnlyDictionary<string, string>? BuyerFiscalIds = null, string? PaymentMethodKey = null);

public record ReceiptAllocationRequest(Guid InvoiceId, decimal Amount);

public partial record CreateReceiptRequest(
    Guid CustomerId, Guid BranchId, decimal Amount, string MethodKey,
    DateTime? ReceivedAtUtc = null,
    string? Reference = null,
    string? InstrumentNumber = null,
    string? InstrumentBank = null,
    DateTime? InstrumentDueDate = null,
    string? Notes = null,
    IReadOnlyList<ReceiptAllocationRequest>? Allocations = null,
    // The "in exchange for" line on the printed voucher, e.g. the contract number and period.
    string? Purpose = null,
    // What the money is for: "payment" (the default), "repair" or "loss" (answers 19, 26). A repair
    // or a loss names the contract and the return note that recorded it; a payment may name its contract.
    string? Kind = null, Guid? ContractId = null, Guid? ReturnNoteId = null,
    // M7b, an advance ("advance", only while advances are held on 419): the VAT rate the seller ticked when the accountant
    // made an advance's VAT due on receipt (null or 0: untaxed, the default), and the customer maintenance job it is for (MS-35).
    decimal? AdvanceTaxRate = null, Guid? MaintenanceJobId = null);

public record SettleReceiptRequest(string Status, string? Note = null);

/// <summary>
/// M7b: part of an advance held on 419 applied to one of the customer's invoices, or — with no invoice — released to the
/// customer's account 411. No amount: as much as the advance still holds, and no more than the invoice still owes.
/// </summary>
public record ApplyAdvanceRequest(Guid? InvoiceId = null, decimal? Amount = null);

/// <summary>
/// One application or release of an advance (M7b), or a part of a receipt kept as other income (SAL-13,
/// <paramref name="Kind"/> = <c>Kept</c>, with its <paramref name="Reason"/> and the income account credited).
/// <paramref name="TaxAmount"/>: the advance's VAT given back with an application; on a kept part, the VAT that stays due.
/// </summary>
public record AdvanceApplicationDto(
    Guid Id, Guid? InvoiceId, string? InvoiceNumber, decimal Amount, decimal TaxAmount, DateTime AppliedAtUtc, string? CreatedBy,
    string Kind = "Application", string? Reason = null, string? IncomeAccountCode = null);

/// <summary>
/// SAL-13: part of a customer's money kept on a cancelled order, moved to the income account the accountant chose. No amount:
/// all the advance still holds (on 419), or all of the receipt's unallocated credit the customer still has (on 411).
/// The reason is required.
/// </summary>
public record KeepAdvanceRequest(decimal? Amount = null, string? Reason = null);

/// <summary>An income (class 7) account a kept advance may be credited to (SAL-13), with its names.</summary>
public record IncomeAccountChoiceDto(string Code, string NameAr, string NameFr, string NameEn);

/// <summary>
/// The debtor book's accounting choices (M7b): where an advance is held (<c>CustomerAccount</c> = 411, the default, or
/// <c>AdvancesAccount</c> = 419) and when its VAT is due (<c>AtInvoice</c>, the default, or <c>WhenReceived</c>).
/// <paramref name="AdvancesAccountReady"/>: account 419 is in the chart and active (the 419 option is refused otherwise).
/// <paramref name="OpenAdvances"/>: confirmed advances still held on 419, all customers.
/// SAL-13: <paramref name="KeptAdvances"/> (<c>NotAllowed</c>, the default, or <c>OtherIncome</c>), the income account a kept
/// advance is credited to, and the active income accounts it may be chosen from.
/// </summary>
public record ReceivableSettingsDto(
    string AdvancesHeldOn, string AdvanceVat, bool AdvancesAccountReady, decimal OpenAdvances,
    DateTime? UpdatedAtUtc, string? UpdatedBy,
    string KeptAdvances = "NotAllowed", string? KeptIncomeAccount = null,
    IReadOnlyList<IncomeAccountChoiceDto>? IncomeAccounts = null);

/// <summary>SAL-13's two fields left out (null) keep what is saved, so an older client never resets them.</summary>
public record UpdateReceivableSettingsRequest(
    string? AdvancesHeldOn, string? AdvanceVat, string? KeptAdvances = null, string? KeptIncomeAccount = null);

public record ReceiptAllocationDto(Guid InvoiceId, string? InvoiceNumber, decimal Amount);

/// <summary>
/// <paramref name="Unallocated"/> is money on account: received, counted against the customer's
/// balance, and not yet written against any particular invoice.
/// </summary>
public record ReceiptVoucherDto(
    Guid Id, string Number, Guid CustomerId, string? CustomerName, Guid BranchId,
    DateTime ReceivedAtUtc, string MethodKey, string Behavior, string Status,
    decimal Amount, decimal Allocated, decimal Unallocated,
    DateTime? ConfirmedAtUtc, string? StatusNote, string? Reference,
    string? InstrumentNumber, string? InstrumentBank, DateTime? InstrumentDueDate,
    string? Notes, IReadOnlyList<ReceiptAllocationDto> Allocations,
    string? Purpose = null,
    string Kind = "Payment", Guid? ContractId = null, string? ContractNumber = null,
    Guid? ReturnNoteId = null, string? ReturnNoteNumber = null,
    // M7b, an advance only: its VAT rate and amount posted on receipt (0 when untaxed or due at the invoice), what is still
    // held on 419 (0 until it is confirmed), its maintenance job (MS-35) and every application or release so far.
    decimal AdvanceTaxRate = 0m, decimal AdvanceTaxAmount = 0m, decimal AdvanceOpen = 0m,
    Guid? MaintenanceJobId = null, string? MaintenanceJobNumber = null,
    IReadOnlyList<AdvanceApplicationDto>? AdvanceApplications = null,
    // SAL-13: how much of this receipt was kept as other income on a cancelled order (off Unallocated, or off AdvanceOpen).
    decimal Kept = 0m);

/// <summary>
/// One invoice line being given back. An empty list credits the whole invoice. <paramref name="StockReturnedQty"/> (P0.3):
/// the goods of the line that come back into stock with this note — 0, a credit of money only, by default; only on a line
/// that took goods out of stock, at most <paramref name="Qty"/> and at most what the line took out less what came back.
/// </summary>
public record CreditNoteLineRequest(Guid InvoiceLineId, decimal Qty, decimal StockReturnedQty = 0);

public record CreateCreditNoteRequest(
    string Reason, DateTime? IssuedAtUtc = null, IReadOnlyList<CreditNoteLineRequest>? Lines = null);

public record CreditNoteLineDto(
    Guid Id, Guid InvoiceLineId, string Sku, string Name, decimal Qty, decimal UnitPrice,
    decimal TaxRate, decimal LineNet, string IncomeAccountCode, int? Days = null, decimal StockReturnedQty = 0);

public record CreditNoteDto(
    Guid Id, string Number, Guid InvoiceId, string? InvoiceNumber, Guid CustomerId, Guid BranchId,
    DateTime IssuedAtUtc, string Reason,
    decimal SubTotalHt, decimal TaxTotal, decimal GrandTotalTtc,
    IReadOnlyList<CreditNoteLineDto> Lines,
    string? SourceType = null, Guid? SourceId = null);

/// <summary>
/// One movement on the customer's trade account, labelled with the document behind it.
/// <paramref name="InvoiceNumber"/> is set when the sale or rental note on this line has since
/// been invoiced, so the customer can match the row against the paper they hold.
/// </summary>
/// <param name="Kind">What the line is (<see cref="ParadisERP.Application.Receivables.CustomerStatementKinds"/>): sale, rental, receipt, payment, opening, adjustment or other.</param>
/// <param name="PaidAtTill">
/// For a sale: what the till took for it on the spot (cash, card, gift card, a cheque handed over),
/// already netted in <paramref name="Credit"/> — for a refund, what it paid back, netted in
/// <paramref name="Debit"/>. Zero for every other line.
/// </param>
public record CustomerStatementRowDto(
    DateTime Date, string SourceType, Guid SourceId,
    string? DocumentNumber, string? InvoiceNumber, string? Description, bool IsReversal,
    decimal Debit, decimal Credit, decimal Balance,
    string Kind = "other", decimal PaidAtTill = 0m);

/// <summary>
/// Opening balance, every document and payment in the period, closing balance — read from
/// account 411 for this customer, which is what makes it reconcile with the books by
/// construction rather than by coincidence.
/// </summary>
/// <param name="PendingNotes">
/// Uncleared paper (account 413). Owed by the customer, and deliberately not subtracted from the
/// trade balance: a pending cheque does not reduce a debt (§5.4).
/// </param>
/// <param name="RecordedBalance">
/// The denormalised balance the credit-limit warning reads. It should equal
/// <paramref name="ClosingBalance"/> plus <paramref name="PendingNotes"/>;
/// <paramref name="BalanceDifference"/> is stated so a drift is a figure rather than a discovery.
/// </param>
/// <param name="UninvoicedBalance">
/// Trade debt with no invoice behind it — a credit sale nobody has raised paper for. It is the
/// part of the balance the ageing report cannot see, stated rather than left to be worked out.
/// </param>
/// <param name="Filter">
/// "all", "sales" or "rentals". When narrowed, the opening and closing balances, the totals and the
/// rows are that part of the account only; the reconciliation figures (pending notes, the recorded
/// balance and its difference, the invoiced and uninvoiced amounts) are always the whole account's.
/// </param>
public record CustomerStatementDto(
    Guid CustomerId, string CustomerCode, string CustomerName,
    DateTime From, DateTime To,
    decimal OpeningBalance, decimal ClosingBalance,
    decimal TotalDebit, decimal TotalCredit,
    IReadOnlyList<CustomerStatementRowDto> Rows,
    decimal PendingNotes, decimal TotalOwed,
    decimal RecordedBalance, decimal BalanceDifference,
    decimal CreditLimit,
    decimal InvoicedOutstanding, decimal UninvoicedBalance,
    string Filter = "all");

/// <summary>
/// <paramref name="CreditBalance"/> is money the company owes this customer — an invoice
/// credited past what was still outstanding on it. Kept out of the buckets, because a credit is
/// not a debt of negative age and netting it off would quietly reduce what somebody else owes.
/// </summary>
public record CustomerAgeingDto(
    Guid CustomerId, string CustomerCode, string CustomerName, decimal CreditLimit,
    decimal Total,
    decimal Current, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90,
    int OldestDaysOverdue, decimal CreditBalance,
    decimal LedgerBalance, decimal Uninvoiced, bool OverCreditLimit);

public record DebtAgeingDto(
    DateTime AsOf,
    decimal Total,
    decimal Current, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90,
    decimal CreditBalance, decimal LedgerBalance, decimal Uninvoiced,
    IReadOnlyList<CustomerAgeingDto> Customers);

// ---------- personnel and payroll (docs/07 §5) ----------
//
// Enums travel as their C# names, the way `UserDto.Role` and `PaymentMethodDto.Behavior`
// already do: a screen that shows a state in words needs a word, and an int on the wire would
// have the UI holding a second copy of the mapping. Dates that are dates — hired on, a working
// day — are `DateOnly` and serialise as `2026-09-24`; instants keep `…Utc` in the name.

/// <summary>One employee's value for one of the company's allowance definitions.</summary>
public record EmployeeAllowanceDto(string AllowanceKey, decimal Value, bool IsEnabled = true);

/// <summary>
/// A person the company pays. <paramref name="AttendanceMode"/> is the mode actually in force —
/// the override when there is one, the company default otherwise — so a screen never has to
/// resolve it; <paramref name="AttendanceModeOverride"/> is what is stored, null meaning "follow
/// the company".
/// </summary>
public record EmployeeDto(
    Guid Id, string Number, string FullName,
    Guid BranchId, string BranchName,
    string PayBasis, decimal BaseRate, string PayoutMethod,
    DateOnly HiredOn, bool IsActive,
    DateOnly? LeftOn = null,
    string? NationalId = null, string? SocialSecurityNumber = null, DateOnly? BirthDate = null,
    string? Phone = null, string? Address = null, string? JobTitle = null,
    string? BankAccount = null,
    string? AttendanceModeOverride = null, string? AttendanceMode = null,
    string? Notes = null,
    IReadOnlyList<EmployeeAllowanceDto>? Allowances = null,
    decimal OpenAdvances = 0,
    string? FingerprintNumber = null,
    // The employee's active account, if they have one: its id and the name it signs in with (the
    // Login ID, or a legacy username). Null for an employee with no access to the application, which
    // is when the employee screen offers "give access".
    Guid? AccountUserId = null,
    string? AccountSignIn = null,
    // Optional, stored as typed (trimmed); null when none was given. Nothing is sent to it.
    string? Email = null,
    // The two parts FullName is composed of, first name first (PinSay #301). A person filed before
    // the parts existed has no first name and the whole name as the family name.
    string? FirstName = null, string? FamilyName = null);

/// <summary>
/// Create and edit both. <c>Number</c> is absent on purpose: it is generated server-side, and
/// letting a screen propose one is how two people end up sharing EMP-000007.
/// </summary>
/// <param name="PayBasis">
/// <c>Monthly</c>; blank on a create means Monthly, and on an edit leaves the stored basis as it is.
/// <c>Daily</c> is refused for anyone not already on it (owner, 2026-10-05: there are no daily
/// workers on payroll — a day worker is paid by payment voucher under Daily labour).
/// </param>
/// <param name="FirstName">
/// With <paramref name="FamilyName"/>, the name in two parts (owner, PinSay #301): when either is
/// sent, both are required and <c>FullName</c> is composed from them, first name first, whatever
/// <paramref name="FullName"/> says. When neither is sent (an older client), <paramref name="FullName"/>
/// is the name, filed whole as the family name, as the migration filed everyone before.
/// </param>
/// <param name="Email">
/// Optional. Null or absent leaves the stored address as it is — the convention <paramref name="Allowances"/>
/// already follows, so a form that predates the field cannot wipe it; blank clears it; anything
/// else must look like an address.
/// </param>
public partial record UpsertEmployeeRequest(
    string FullName, Guid BranchId,
    string PayBasis, decimal BaseRate, string PayoutMethod,
    DateOnly HiredOn, bool IsActive = true,
    DateOnly? LeftOn = null,
    string? NationalId = null, string? SocialSecurityNumber = null, DateOnly? BirthDate = null,
    string? Phone = null, string? Address = null, string? JobTitle = null,
    string? BankAccount = null, string? AttendanceModeOverride = null,
    string? Notes = null,
    IReadOnlyList<EmployeeAllowanceDto>? Allowances = null,
    string? FingerprintNumber = null,
    string? Email = null,
    string? FirstName = null, string? FamilyName = null);

/// <summary>Overtime hours for one band, declared or recorded.</summary>
public record AttendanceOvertimeDto(string RuleKey, decimal Hours);

/// <summary>
/// A foreman's period totals for one employee. The period is a calendar month in the company's
/// time zone, carried as two ints for exactly that reason.
/// </summary>
public record AttendanceDeclarationDto(
    Guid EmployeeId, string EmployeeNumber, string EmployeeName,
    int PeriodYear, int PeriodMonth,
    decimal DaysPresent, decimal PaidLeaveDays, decimal UnpaidAbsenceDays,
    IReadOnlyList<AttendanceOvertimeDto>? Overtime = null,
    string? UpdatedBy = null, DateTime? UpdatedAtUtc = null);

public record AttendanceDeclarationRowRequest(
    Guid EmployeeId,
    decimal DaysPresent, decimal PaidLeaveDays, decimal UnpaidAbsenceDays,
    IReadOnlyList<AttendanceOvertimeDto>? Overtime = null);

/// <summary>The sheet the whole branch is submitted from: one upsert replaces the rows it names.</summary>
public partial record UpsertAttendanceSheetRequest(
    Guid BranchId, int PeriodYear, int PeriodMonth,
    IReadOnlyList<AttendanceDeclarationRowRequest> Rows);

/// <summary>One employee's one day. <c>Status</c> is an <c>AttendanceDayStatus</c> name.</summary>
public record AttendanceDayDto(
    Guid EmployeeId, DateOnly WorkDate, string Status,
    decimal? HoursWorked = null,
    IReadOnlyList<AttendanceOvertimeDto>? Overtime = null,
    string? UpdatedBy = null, DateTime? UpdatedAtUtc = null);

public record UpsertAttendanceDaysRequest(IReadOnlyList<AttendanceDayDto> Days);

/// <summary>A raw punch. <c>Kind</c> is <c>In</c> or <c>Out</c>.</summary>
public record ClockEventDto(
    Guid Id, Guid EmployeeId, DateTime AtUtc, string Kind,
    string? Source = null, string? CreatedBy = null);

public record RecordClockEventRequest(Guid EmployeeId, string Kind, DateTime? AtUtc = null, string? Source = null);

/// <summary>
/// Cash drawn against wages. <c>Outstanding</c> is <c>Amount − Recovered</c>, sent so the list
/// does not make every row do the subtraction.
/// </summary>
public record WageAdvanceDto(
    Guid Id, string Number, Guid EmployeeId, string EmployeeNumber, string EmployeeName,
    Guid BranchId, string BranchName,
    decimal Amount, decimal Recovered, decimal Outstanding,
    DateTime GrantedAtUtc, string PayoutMethod, string Status,
    Guid? CashBoxId = null, Guid? RecoveredInRunId = null, string? RecoveredInRunNumber = null,
    string? Reference = null, string? Note = null, string? CreatedBy = null,
    bool IsInterBranch = false);

/// <summary>
/// Granting one. A box short of the amount warns and pays — the safe is refilled that morning,
/// and the first rule of this product is that nothing blocks on a figure looking wrong.
/// </summary>
public partial record GrantWageAdvanceRequest(
    Guid EmployeeId, decimal Amount, string PayoutMethod,
    Guid? CashBoxId = null, DateTime? GrantedAtUtc = null,
    string? Reference = null, string? Note = null);

/// <summary>One line of one payslip, as the paper prints it.</summary>
public record PayrollElementDto(
    string Key, string Kind, decimal Amount,
    decimal? Qty = null, decimal? Rate = null,
    bool InSocialBase = false, bool InTaxBase = false, int SortOrder = 0);

/// <summary>
/// One employee's payslip inside a run. Every name and number here is the snapshot taken at
/// compute time, not a join: an employee edit never rewrites a payslip.
/// </summary>
public record PayrollRunLineDto(
    Guid Id, Guid EmployeeId,
    string EmployeeNumber, string EmployeeName, string? SocialNumber,
    Guid BranchId,
    string PayBasis, decimal BaseRate,
    decimal DaysPresent, decimal PaidLeaveDays, decimal UnpaidAbsenceDays, string AttendanceSource,
    decimal Gross, decimal SocialBase, decimal SocialEmployee, decimal SocialEmployer,
    decimal TaxBase, decimal IncomeTax,
    decimal AdvanceRecovered, decimal OtherDeductions, decimal Net, decimal PaidAmount,
    IReadOnlyList<PayrollElementDto>? Elements = null,
    IReadOnlyList<string>? Warnings = null,
    Guid? WagePaymentId = null,
    // Workers' bonuses already handed over by payment voucher, taken back off net pay (owner, 2026-11-08).
    decimal PaidByVoucher = 0);

/// <summary>
/// A one-off figure for one person in one run — an input re-read on every compute, never an
/// element, so a recompute keeps it.
/// </summary>
public record PayrollAdjustmentDto(
    Guid Id, Guid RunId, Guid EmployeeId, string EmployeeName,
    string Key, decimal Amount, string Reason,
    bool InSocialBase = false, bool InTaxBase = false,
    string? CreatedBy = null, DateTime? CreatedAtUtc = null);

public record UpsertPayrollAdjustmentRequest(
    Guid EmployeeId, string Key, decimal Amount, string Reason,
    bool InSocialBase = false, bool InTaxBase = false);

/// <summary>
/// One month's wages for one branch. <c>IsProvisional</c> is frozen from the settings snapshot:
/// a run computed while the statutory figures were still the admin's unconfirmed placeholders
/// says so on screen and on paper, and goes on saying so afterwards.
/// </summary>
public record PayrollRunDto(
    Guid Id, string Number, Guid BranchId, string BranchName,
    int PeriodYear, int PeriodMonth, string Status,
    int HeadCount, int CurrencyDecimals, bool IsProvisional,
    decimal Gross, decimal SocialBase, decimal SocialEmployee, decimal SocialEmployer,
    decimal TaxBase, decimal IncomeTax, decimal EmployerLevies,
    decimal AdvancesRecovered, decimal OtherDeductions, decimal Net,
    decimal PaidTotal = 0,
    DateTime? ComputedAtUtc = null, string? LockedBy = null,
    DateTime? ApprovedAtUtc = null, string? ApprovedBy = null, DateTime? PaidAtUtc = null,
    DateTime? ReversedAtUtc = null, string? ReversedBy = null, string? ReversalReason = null,
    Guid? LedgerEntryId = null, Guid? ReversalEntryId = null,
    string? CreatedBy = null, DateTime? CreatedAtUtc = null,
    IReadOnlyList<PayrollRunLineDto>? Lines = null,
    IReadOnlyList<PayrollAdjustmentDto>? Adjustments = null,
    IReadOnlyList<string>? Warnings = null,
    decimal PaidByVoucher = 0);

public partial record CreatePayrollRunRequest(Guid BranchId, int PeriodYear, int PeriodMonth);

/// <summary>A reason is required, and it is what the reversed run carries to explain itself.</summary>
public record ReversePayrollRunRequest(string Reason);

public record WagePaymentLineDto(Guid RunLineId, Guid EmployeeId, string EmployeeName, decimal Amount);

/// <summary>Money actually handed over against an approved run.</summary>
public record WagePaymentDto(
    Guid Id, string Number, Guid RunId, string RunNumber,
    Guid BranchId, string BranchName,
    DateTime PaidAtUtc, string PayoutMethod, decimal Amount,
    Guid? CashBoxId = null, string? Reference = null, string? Note = null,
    string? CreatedBy = null, Guid? LedgerEntryId = null,
    IReadOnlyList<WagePaymentLineDto>? Lines = null);

public record WagePaymentLineRequest(Guid RunLineId, decimal Amount);

/// <summary>
/// Paying part or all of a run. <c>Lines</c> empty means every unpaid line in full, which is the
/// ordinary case and saves the screen sending nine hundred rows to say "all of them".
/// </summary>
public partial record PayWagesRequest(
    Guid RunId, string PayoutMethod,
    Guid? CashBoxId = null, DateTime? PaidAtUtc = null,
    string? Reference = null, string? Note = null,
    IReadOnlyList<WagePaymentLineRequest>? Lines = null);

/// <summary>
/// The accountant recording that the social fund or the tax office was actually paid, so the
/// payroll liabilities stop reading as debt. <c>AccountCode</c> is a ledger account code, refused
/// if the chart does not know it.
/// </summary>
public record PayrollStatutoryPaymentDto(
    Guid Id, Guid BranchId, string BranchName,
    string AccountCode, string AccountName,
    decimal Amount, DateTime PaidAtUtc, string PayoutMethod,
    Guid? CashBoxId = null, int? PeriodYear = null, int? PeriodMonth = null,
    string? Reference = null, string? Note = null,
    string? CreatedBy = null, Guid? LedgerEntryId = null);

public partial record RecordStatutoryPaymentRequest(
    Guid BranchId, string AccountCode, decimal Amount, string PayoutMethod,
    DateTime? PaidAtUtc = null, Guid? CashBoxId = null,
    int? PeriodYear = null, int? PeriodMonth = null,
    string? Reference = null, string? Note = null);
