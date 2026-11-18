using System.ComponentModel.DataAnnotations;

namespace ParadiERP.Domain;

/// <summary>
/// Base for every entity that is replicated down to POS terminals.
/// <see cref="Version"/> is a globally monotonic counter (see SyncCounter) used as the
/// delta-sync cursor. Rows are never hard-deleted; they are tombstoned via IsDeleted so
/// terminals can remove them locally on the next pull.
/// </summary>
public abstract class SyncEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Version { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
}

public enum UserRole { Cashier = 0, Manager = 1, Admin = 2 }

public class User : SyncEntity
{
    [MaxLength(64)] public string Username { get; set; } = string.Empty;
    [MaxLength(200)] public string FullName { get; set; } = string.Empty;
    /// <summary>BCrypt hash of the web/admin password. Never leaves the server.</summary>
    [MaxLength(200)] public string PasswordHash { get; set; } = string.Empty;
    /// <summary>BCrypt hash of the numeric POS unlock PIN. Replicated so cashiers can log in offline.</summary>
    [MaxLength(200)] public string PinHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Cashier;
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Named permissions granted on top of the role, comma separated. Empty for almost
    /// everyone — see <see cref="Permissions"/> for why this exists at all.
    /// </summary>
    [MaxLength(400)] public string? Permissions { get; set; }

    /// <summary>
    /// The one branch a cashier may open a session at (docs/07 §2.2). Null means any terminal,
    /// which is what every account that predates the column gets, so nothing is locked out on
    /// upgrade. A real foreign key with Restrict: a branch that still binds a login cannot be
    /// deleted and a stale id cannot be written.
    /// <para>
    /// <b>The terminal binding is cashiers only</b>; a manager or admin is never refused at login.
    /// Since 2026-09-30 it is also, for every role, the branch the user works at (BranchLock):
    /// required for a new user, never cleared, and for everyone but the Admin the only branch
    /// their screens offer and the branch their documents are raised at. Null on a legacy row
    /// means the main branch. It is a login's branch, not the payroll employee's: an employee is
    /// not a login.
    /// </para>
    /// </summary>
    public Guid? BranchId { get; set; }

    /// <summary>
    /// The user's role in the configurable model (AUTH-1). Until Step 5 it is written only from
    /// <see cref="Role"/> — on every save, by the roles mirror — so it can never disagree with the
    /// legacy column that still decides access. Null only before the upgrade has run.
    /// </summary>
    public Guid? RoleId { get; set; }

    /// <summary>
    /// Moves whenever what this user may do changes on their own row — their role, or an additional
    /// grant — never for anything else, and never written by hand: every save that changes one bumps
    /// it in the database, in the same transaction (AUTH-1 Step 3). With the role's own
    /// <see cref="Role.Version"/> it stamps the server's cached decision, so a stale one is never used.
    /// Unlike <see cref="SyncEntity.Version"/> it revokes no session.
    /// </summary>
    public long AuthorizationVersion { get; set; }

    /// <summary>
    /// The HR employee this account belongs to (owner, 2026-10-05: an account is only ever given to
    /// an employee). Null on the bootstrap administrator and on accounts that predate the rule; the
    /// users list asks for those to be linked. Restrict: an employee is never deleted, and one with
    /// an account is in use. At most one ACTIVE, undeleted account per employee — the filtered
    /// unique index enforces it, the endpoint says it in words first.
    /// </summary>
    public Guid? EmployeeId { get; set; }

    /// <summary>
    /// The sign-in number the server drew for this account when it was created: exactly 8 digits,
    /// random, never starting with 0, unique company-wide (<c>Infrastructure/Auth/LoginIds</c>).
    /// <see cref="Username"/> carries the same value, so signing in is unchanged; this column says
    /// the name is a Login ID rather than a legacy username, and it never changes. Null on legacy
    /// accounts, which keep signing in with the username they had. It is not the employee number
    /// and not the fingerprint number — three identifiers, never reused for one another.
    /// </summary>
    [MaxLength(8)] public string? LoginId { get; set; }

    // ---------- two-step login (owner, 2026-11-11: optional, each user's own choice) ----------
    // None of these is replicated: the sync payload names the user's columns one by one
    // (SyncService), and a till's offline PIN unlock never asks for a code.

    /// <summary>
    /// The authenticator app's shared key, protected with the server's Data Protection keys
    /// (Infrastructure/Auth/TwoFactor). Null while two-step login is off.
    /// </summary>
    [MaxLength(500)] public string? TwoFactorSecret { get; set; }

    /// <summary>A key offered by the setup and not yet confirmed with a code. Protected the same way.</summary>
    [MaxLength(500)] public string? TwoFactorPendingSecret { get; set; }

    /// <summary>When two-step login was turned on; null means off.</summary>
    public DateTime? TwoFactorEnabledAtUtc { get; set; }

    /// <summary>The last 30-second step a code was accepted for, so a code is never accepted twice.</summary>
    public long TwoFactorLastStep { get; set; }

    /// <summary>The unused recovery codes, each a SHA-256 hash in hex, comma separated.</summary>
    [MaxLength(1000)] public string? TwoFactorRecoveryCodes { get; set; }

    /// <summary>
    /// Stamped into every "trust this computer" token. Drawn again whenever two-step login is
    /// turned on or off, reset by an administrator, or the password changes — which forgets every
    /// trusted computer at once.
    /// </summary>
    [MaxLength(32)] public string? TwoFactorTrustStamp { get; set; }

    /// <summary>
    /// The user's own photo (owner, 2026-11-11), a path in the files folder (FilePaths.UserPhoto);
    /// the bytes never go into the database.
    /// </summary>
    [MaxLength(200)] public string? PhotoPath { get; set; }
}

public class Store : SyncEntity
{
    [MaxLength(200)] public string NameAr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameFr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameEn { get; set; } = string.Empty;
    [MaxLength(400)] public string Address { get; set; } = string.Empty;
    [MaxLength(400)] public string AddressAr { get; set; } = string.Empty;
    [MaxLength(400)] public string AddressFr { get; set; } = string.Empty;
    [MaxLength(400)] public string AddressEn { get; set; } = string.Empty;
    [MaxLength(60)] public string Phone { get; set; } = string.Empty;
    /// <summary>
    /// Values of the fiscal identifiers the company's country profile defines (NIF, ICE,
    /// SIRET...), keyed by the profile's identifier key. Currency and tax rules are not here:
    /// they belong to the operator-owned country profile.
    /// </summary>
    public Dictionary<string, string> FiscalIds { get; set; } = new();
    [MaxLength(400)] public string ReceiptFooter { get; set; } = string.Empty;
    [MaxLength(400)] public string ReceiptFooterAr { get; set; } = string.Empty;
    [MaxLength(400)] public string ReceiptFooterFr { get; set; } = string.Empty;
    [MaxLength(400)] public string ReceiptFooterEn { get; set; } = string.Empty;

    // Printed documents (docs/07 §4.3). Admin-owned like the receipt footer, and — unlike it —
    // deliberately absent from StoreDto and UpsertStoreRequest: paragraphs of contract terms,
    // payment terms and bank details have no reader on a till, and a replicated field nothing
    // reads is dead wiring. The PUT that writes them uses a plain SaveChanges, never
    // SaveWithVersions, so the synced Store row is not re-pulled for fields it does not carry.

    /// <summary>
    /// The text printed at the foot of each document kind, per language:
    /// <c>{ [kind]: { [lang]: text } }</c> with the kinds of
    /// <c>ParadiERP.Application.Contracts.DocumentTextKinds</c> and up to
    /// <see cref="DocumentTextMaxLength"/> characters per text. Null on every row that predates
    /// the column and is read as empty; the code writes no legal terms, the owner pastes his own.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>>? DocumentTexts { get; set; }

    /// <summary>The longest a single document text may be. Enforced in code; the column is unbounded JSON.</summary>
    public const int DocumentTextMaxLength = 4000;

    /// <summary>
    /// The company's other rental-contract wordings, beyond the standard one at
    /// <c>DocumentTexts["rentalContract"]</c> (owner, PinSay #302, 2026-10-05: "enable multi contracts
    /// and enhance the creation to be as template"). Each is a name the contract form offers and a key;
    /// its text lives in <see cref="DocumentTexts"/> under that key (<c>rentalContract.&lt;id&gt;</c>), so
    /// it is counted against the yearly allowance and keeps its history exactly as the standard wording
    /// does. A contract names the one it prints (<see cref="RentalContract.WordingKey"/>). In list order;
    /// null on every row that predates the column, and read as none.
    /// </summary>
    public List<ContractWording>? ContractWordings { get; set; }

    /// <summary>
    /// How many times each document kind's wording may be rewritten in one calendar year, counted
    /// in the company's time zone. One save is one change however many languages it rewrote — that
    /// is how the owner thinks of it: he changed the contract. A save that changes nothing consumes
    /// nothing. Every kind has its own count.
    ///
    /// <para>
    /// <b>This is the owner's own discipline, not a control against him.</b> He asked for the limit
    /// and he asked for it to be a setting he sets — so it lives here, editable from the back
    /// office, and he can raise it whenever he wants. What actually protects the customer's copy is
    /// not this number but <see cref="DocumentTextChange"/>: every rewrite keeps its predecessor, so
    /// the wording somebody signed under stays readable however often the current text moves. The
    /// limit exists to make a rewrite a deliberate act, and nothing here should be hardened into a
    /// lock a future maintainer cannot open.
    /// </para>
    /// <para>
    /// Three by default, which is the figure the owner named, and the value every row that predates
    /// the column reads. Zero is a legitimate setting and means "frozen"; the code refuses a
    /// negative one rather than storing it.
    /// </para>
    /// </summary>
    public int DocumentTextChangesPerYear { get; set; } = DefaultDocumentTextChangesPerYear;

    /// <summary>The owner's figure, and the default on every existing row. Not a constant of the domain.</summary>
    public const int DefaultDocumentTextChangesPerYear = 3;

    [MaxLength(200)] public string? Email { get; set; }
    [MaxLength(200)] public string? Website { get; set; }

    /// <summary>
    /// Free text, because RIB, IBAN and BIC labelling differs by country and a field per
    /// country would be the hardcoded country rule the profile exists to prevent.
    /// </summary>
    [MaxLength(400)] public string? BankDetails { get; set; }

    /// <summary>
    /// The languages every printed document's title is set in, as comma-separated codes in
    /// print order ("ar,fr"): the owner's paper heads a return note "BON DE RETOUR" with
    /// "إذن إرجاع سلعة" beside it. The document language still governs the rest of the sheet;
    /// these only add the title in the other languages listed. Null — every row that predates
    /// the column — prints the title in the document language alone, as before. A setting, not
    /// a pair of constants: which two languages a company's paper carries is its country's.
    /// </summary>
    [MaxLength(40)] public string? DocumentTitleLanguages { get; set; }

    /// <summary>
    /// The languages an item's name prints in on the rental papers, comma-separated in order: the
    /// first is the name, a second prints beneath it (owner, 2026-10-02, answer 28: "one name,
    /// Arabic or French, the admin chooses — or both"). Null keeps what the papers did before the
    /// setting: the name signed in French with the Arabic beneath.
    /// </summary>
    [MaxLength(40)] public string? DocumentItemNameLanguages { get; set; }

    /// <summary>
    /// The expense categories whose payment voucher must name a rental contract, comma-separated
    /// (owner, 2026-10-02, answer 26: "depending on the item, choosing the contract is mandatory or
    /// not"). Null or empty: no expense requires one, and any may still name one.
    /// </summary>
    [MaxLength(300)] public string? VoucherContractCategories { get; set; }

    /// <summary>
    /// The interface face this company's screens use, as one of the bundled font keys (see
    /// Application/Regional/UiFonts). Null means the country profile's default. It sits on the
    /// Store rather than in the profile because it is the company's taste, not a statutory
    /// figure — and it replicates with the Store, so a till renders it offline.
    /// </summary>
    [MaxLength(40)] public string? UiFont { get; set; }

    // Admin-configurable customer intake policy: which fields a back-office user must fill
    // in before a customer record can be saved. None are required by default so a shop can
    // start selling immediately and tighten data quality later.
    public bool RequireCustomerEmail { get; set; }
    public bool RequireCustomerBirthDate { get; set; }
    public bool RequireCustomerPhone { get; set; }
    public bool RequireCustomerAddress { get; set; }
    public bool RequireCustomerTaxId { get; set; }

    // Loyalty programme (company business rules; whether the Loyalty tender exists at all is
    // the operator's country profile).
    public bool LoyaltyEnabled { get; set; }
    /// <summary>Points earned per 1 currency unit of eligible sale amount.</summary>
    public decimal LoyaltyPointsPerUnit { get; set; }
    /// <summary>Currency value of one point when redeemed.</summary>
    public decimal LoyaltyPointValue { get; set; }
    public int LoyaltyMinRedeemPoints { get; set; }

    // Renting (company business rules, owned by the admin — exactly like the loyalty
    // programme above). Design §5.5 describes both of these and gives them nowhere to live;
    // the rental engine names their absence as the gap that stops it being fed real data.
    //
    // They are deliberately kept out of StoreDto and therefore out of the sync pull: renting
    // is back-office and does not touch the sync engine (§5.9), and a till has no use for an
    // overdue multiplier it can never apply.

    /// <summary>
    /// What the overdue price is, as a multiple of the rental price — 1.5× and so on.
    ///
    /// <para>
    /// A multiplier rather than a second price list, because prices move and a second list
    /// kept by hand drifts out of step silently (§7 decision 4). It is applied once, when the
    /// contract is signed, and the resulting figure is frozen on the line; changing it
    /// afterwards never changes a signed contract.
    /// </para>
    /// <para>Never below 1: an overdue day cheaper than a rented day pays the customer to be late.</para>
    /// </summary>
    public decimal RentalOverdueMultiplier { get; set; } = 1.5m;

    /// <summary>
    /// The deposit switch (§5.6). Off, and while it is off the deposit is invisible — no
    /// field, no zero line, no report column — and nothing computes with it.
    /// </summary>
    public bool RentalDepositEnabled { get; set; }

    /// <summary>
    /// The rate rental documents are taxed at, as a percentage. <b>0 means hire is not taxed
    /// here</b>, which is the default: whether hire carries tax, and at what rate, is a matter
    /// for the company and its jurisdiction, and inferring a figure would be exactly the kind
    /// of hardcoded tax rule the country profile exists to prevent.
    /// <para>
    /// It is only the default. Each contract line freezes the rate it was signed at, so
    /// changing this never moves money on a contract that already exists.
    /// </para>
    /// </summary>
    public decimal RentalTaxRate { get; set; }

    /// <summary>
    /// Where the company's logo is kept in the files folder (<c>FilePaths.Logo</c>), printed in the
    /// document header; null = no logo. Only the path: the picture itself is never in the database
    /// (owner, 2026-10-04). Kept off <c>StoreDto</c>: a till prints a receipt, not a headed document.
    /// </summary>
    [MaxLength(200)] public string? LogoPath { get; set; }

    /// <summary>
    /// The local hour (0–23) after which a rental day is treated as spent, or null for today's
    /// rule: the delivery day counts, the return day never counts.
    /// <para>
    /// When set: goods delivered at or after this hour start their period the next day; goods
    /// returned at or after this hour have their return day counted by default, and the branch
    /// manager may decide otherwise on the note (<see cref="ReturnNote.CountReturnDay"/>). This is
    /// the owner's rule — "27 to 29 counts two days; the return day is not counted when the goods
    /// come back in the morning, at the branch manager's judgement".
    /// </para>
    /// </summary>
    public int? RentalDayCutoffHour { get; set; }

    /// <summary>
    /// The minimum hire in days (owner, early return decision B). Goods back sooner are charged
    /// this many days, or their agreed period when that is shorter; only the days beyond are given
    /// back. 0, the default, charges the days used. Frozen on each contract when it is raised
    /// (<see cref="RentalContract.MinimumRentalDays"/>), so a change reaches new contracts only.
    /// </summary>
    public int RentalMinimumDays { get; set; }
}

/// <summary>
/// A physical location of the company, of one of two kinds (owner, 2026-10-05):
/// <list type="bullet">
/// <item>a selling <b>branch</b> — sells, holds tills and a cash box;</item>
/// <item>a <b>warehouse</b> (<see cref="IsWarehouse"/>) — holds the stock, receives it from
/// suppliers and transfers it to the branches via <see cref="StockMovement"/> transfers. It never
/// sells and handles no money: no till, no cashier and no cash box.</item>
/// </list>
/// Of each kind exactly one is the main one (<see cref="IsMain"/>): the main branch is the head
/// office, where the first till goes and whose cash box is the central safe; the main warehouse is
/// where supplier deliveries go and transfers start by default. Both exist from a company's first
/// start and are kept so at every startup (<c>DefaultBranches</c>). Replicated to terminals like
/// the rest of the catalog so a till can show which branch it belongs to.
/// </summary>
public class Branch : SyncEntity
{
    /// <summary>
    /// The location's three-digit code, 100 to 999 — never a leading zero (owner, 2026-10-05; three
    /// digits since 2026-10-19) — that the customer and employee codes and the rental contract numbers
    /// carry (<c>Cust-3612610001</c> is a person registered at location 361, <c>CTR-361000001</c> its
    /// first contract). Given by the server to every location, branch or warehouse, when it is first
    /// saved: a random free one, deleted locations included, so a code is never handed out twice.
    /// Fixed for life — an edit never changes it — and unique. Codes drawn before 2026-10-19 had two
    /// digits; the BranchContractNumbers migration recoded them, while the customer and employee codes
    /// already drawn keep the two digits they were given.
    /// </summary>
    [MaxLength(3)] public string Code { get; set; } = string.Empty;

    [MaxLength(200)] public string NameAr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameFr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameEn { get; set; } = string.Empty;
    [MaxLength(400)] public string Address { get; set; } = string.Empty;
    [MaxLength(400)] public string AddressAr { get; set; } = string.Empty;
    [MaxLength(400)] public string AddressFr { get; set; } = string.Empty;
    [MaxLength(400)] public string AddressEn { get; set; } = string.Empty;
    [MaxLength(60)] public string Phone { get; set; } = string.Empty;
    /// <summary>
    /// Set once the branch is trading. Until then it accepts opening balances, because on the
    /// day a company adopts this system every branch already holds stock that never passed
    /// through a receiving note — and forcing it through one would invent a purchase that
    /// never happened. Afterwards the warehouse rule applies and new goods enter centrally.
    /// </summary>
    public bool IsOpenForOperation { get; set; }

    /// <summary>
    /// The main one of its kind: the main branch when <see cref="IsWarehouse"/> is false, the main
    /// warehouse when it is true. Exactly one of each. Until 2026-10-05 the flag meant "the central
    /// warehouse"; the CompanyBranchKinds migration made that branch the main warehouse.
    /// </summary>
    public bool IsMain { get; set; }

    /// <summary>The branch's kind: a warehouse holds and moves stock and never sells — no till, no cashier, no cash box.</summary>
    public bool IsWarehouse { get; set; }

    /// <summary>The head office (not mapped; write <c>IsMain &amp;&amp; !IsWarehouse</c> in queries).</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped] public bool IsMainBranch => IsMain && !IsWarehouse;

    /// <summary>The main warehouse (not mapped; write <c>IsMain &amp;&amp; IsWarehouse</c> in queries).</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped] public bool IsMainWarehouse => IsMain && IsWarehouse;
    /// <summary>
    /// Admin-set policy: whether a manual stock adjustment or transfer may drive this
    /// branch's stock negative. Never affects sales — a sale is never blocked or rejected
    /// for a stock shortfall (see SyncService), only flagged with a warning.
    /// </summary>
    public bool AllowNegativeStock { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The branch's own fiscal identifiers, keyed like <see cref="Store.FiscalIds"/>: each branch
    /// prints its own commercial register, NIF, NIS and article number when it has them. Empty
    /// means the company's are printed instead.
    /// </summary>
    public Dictionary<string, string> FiscalIds { get; set; } = new();

    /// <summary>
    /// Where the branch's own logo is kept in the files folder, shown in the top bar of every
    /// screen with the branch's name (the owner, 2026-09-30). Null = none: the main branch's logo
    /// is shown instead, then the company's, then the text mark. The row carries only the path; a
    /// till fetches the picture through the signed-in logo endpoint and keeps its own copy, so it
    /// draws it offline. Printed documents still carry the company logo.
    /// </summary>
    [MaxLength(200)] public string? LogoPath { get; set; }
}

/// <summary>
/// A product's stock at one branch. Replaces a single company-wide quantity so that two
/// branches selling the same SKU keep independent counts; the main warehouse is just another
/// row in this table, distinguished only by its branch's <see cref="Branch.IsMain"/> flag.
/// </summary>
/// <summary>
/// The states a unit of stock can be in.
///
/// <para>
/// The first four are places it can sit while the company still holds it; only
/// <see cref="Available"/> can be sold, hired out or transferred.
/// <see cref="WrittenOff"/> is not a bucket at all — the quantity leaves, which is precisely
/// the difference between a loss and a state.
/// </para>
/// </summary>
public enum StockState
{
    Available = 0,
    OnRent = 1,
    Maintenance = 2,
    Scrap = 3,
    WrittenOff = 4
}

public class ProductStock
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Product? Product { get; set; }
    public Guid BranchId { get; set; }
    public Branch? Branch { get; set; }

    /// <summary>
    /// What can be sold or hired out right now.
    /// <para>
    /// This is the quantity the system has always tracked — selling decrements it, a transfer
    /// moves it — and it keeps that meaning exactly. The buckets below are the states an item
    /// can be in *instead of* being available; none of them is sellable, and none of them has
    /// left the business.
    /// </para>
    /// </summary>
    public decimal StockQty { get; set; }

    /// <summary>
    /// Out with a customer on a rental. Not sold: it is coming back. A single quantity cannot
    /// express this, which is why renting needs these buckets at all.
    /// </summary>
    public decimal OnRentQty { get; set; }

    /// <summary>Being repaired. Still an asset, not available to hire.</summary>
    public decimal MaintenanceQty { get; set; }

    /// <summary>Damaged beyond use and awaiting sale as scrap. Still worth something.</summary>
    public decimal ScrapQty { get; set; }

    public decimal ReorderLevel { get; set; }

    /// <summary>
    /// Everything the company still holds at this branch. Written off stock is not here: it
    /// has left, which is the difference between a loss and a state.
    /// </summary>
    public decimal OwnedQty => StockQty + OnRentQty + MaintenanceQty + ScrapQty;
}

/// <summary>
/// The state machine behind the buckets on <see cref="ProductStock"/>.
///
/// <para>
/// One copy, on purpose. Issuing a rental moves available → on rent and returning it moves the
/// quantity back or on to maintenance, scrap or out of the business — the same transitions the
/// stock endpoint already performs by hand. Written twice, the two would answer "how much is
/// available" differently the first time either was corrected, and the buckets would stop
/// adding up to what the branch actually holds.
/// </para>
/// </summary>
public static class StockStates
{
    /// <summary>How much of this row sits in a given state. Written-off is never held anywhere.</summary>
    public static decimal Held(this ProductStock stock, StockState state) => state switch
    {
        StockState.Available => stock.StockQty,
        StockState.OnRent => stock.OnRentQty,
        StockState.Maintenance => stock.MaintenanceQty,
        StockState.Scrap => stock.ScrapQty,
        _ => 0
    };

    /// <summary>Adds to (or, with a negative delta, takes from) one bucket.</summary>
    public static void Add(this ProductStock stock, StockState state, decimal delta)
    {
        switch (state)
        {
            case StockState.Available: stock.StockQty += delta; break;
            case StockState.OnRent: stock.OnRentQty += delta; break;
            case StockState.Maintenance: stock.MaintenanceQty += delta; break;
            case StockState.Scrap: stock.ScrapQty += delta; break;
        }
    }

    /// <summary>
    /// Moves a quantity from one state to another on one row.
    ///
    /// <para>
    /// <see cref="StockState.WrittenOff"/> is the one destination that is not a bucket: the
    /// quantity leaves the business entirely rather than landing somewhere else. That is
    /// precisely what separates a loss from a state, and it is why this is a method rather
    /// than two calls to <see cref="Add"/> at every call site.
    /// </para>
    /// </summary>
    public static void Move(this ProductStock stock, StockState from, StockState to, decimal qty)
    {
        stock.Add(from, -qty);
        if (to != StockState.WrittenOff) stock.Add(to, qty);
    }

    /// <summary>
    /// Whether a quantity in this state is still owned. Only the goods that are still owned
    /// belong in the asset figure, which is why a write-off is absent rather than bucketed.
    /// </summary>
    public static bool IsOwned(StockState state) => state != StockState.WrittenOff;
}

public class Category : SyncEntity
{
    [MaxLength(120)] public string NameAr { get; set; } = string.Empty;
    [MaxLength(120)] public string NameFr { get; set; } = string.Empty;
    [MaxLength(120)] public string NameEn { get; set; } = string.Empty;
    [MaxLength(16)] public string Color { get; set; } = "#3b82f6";
    public int SortOrder { get; set; }
}

public class Product : SyncEntity
{
    [MaxLength(64)] public string Sku { get; set; } = string.Empty;
    [MaxLength(64)] public string? Barcode { get; set; }
    [MaxLength(200)] public string NameAr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameFr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameEn { get; set; } = string.Empty;
    public Guid? CategoryId { get; set; }
    public Category? Category { get; set; }
    /// <summary>Shelf price. Tax-inclusive when the country profile's PricesIncludeTax is true.</summary>
    public decimal Price { get; set; }
    public decimal CostPrice { get; set; }

    /// <summary>
    /// What a unit is carried at in the asset register, which is not its purchase price and
    /// not what a customer is charged if they lose one. Scaffolding bought at 1200 may be
    /// carried at 800 and charged out at 2000; none of those three numbers derives from
    /// another. 0 means the item is not tracked as an asset.
    /// </summary>
    public decimal AssetUnitValue { get; set; }

    /// <summary>
    /// Whether this item is hired out rather than sold. Rental items go out and come back, so
    /// only these carry the on-rent, maintenance and scrap balances.
    /// </summary>
    public bool IsRentable { get; set; }

    /// <summary>
    /// The ordinary price for hiring one unit for one day. It is the default placed
    /// on a new contract line, not a price forced onto it: the signed contract still snapshots
    /// the figure the clerk and customer agree. Zero means the rental price has not been set yet.
    /// Kept off the till payload because renting is a back-office workflow.
    /// </summary>
    public decimal RentalUnitPrice { get; set; }

    /// <summary>
    /// This item's own overdue multiplier, overriding the company's (§7 decision 4).
    ///
    /// <para>
    /// Null means "no override" and is the right answer for every product that already exists,
    /// which is why it is nullable rather than defaulted to 1: a zero or a one here would
    /// silently price every overdue day of that item at the ordinary rental rate.
    /// </para>
    /// </summary>
    public decimal? RentalOverdueMultiplier { get; set; }

    /// <summary>
    /// The fixed value charged per unit not returned or wrecked, before tax; the contract line's
    /// <c>DamageUnitPrice</c> starts from it. Null means it has not been set and the clerk types
    /// the figure on the line. Kept off the till payload like every other rental figure.
    /// </summary>
    public decimal? RentalDamageUnitPrice { get; set; }
    /// <summary>Tax percentage; one of the country profile's rates when last edited.</summary>
    public decimal TaxRate { get; set; }
    [MaxLength(16)] public string Unit { get; set; } = "pcs";
    /// <summary>
    /// Whether this SKU is stock-tracked at all. A per-branch quantity/reorder level lives in
    /// <see cref="ProductStock"/>, not here — a global scalar can't represent two branches
    /// selling the same product with independent counts.
    /// </summary>
    public bool TrackStock { get; set; } = true;
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// What the product is to manufacturing (Phase 1). <see cref="ManufacturingRole.None"/> — every product
    /// that predates the column — is ordinary merchandise and behaves exactly as before. Set on the
    /// manufacturing materials screen; a change stamps a new version, so tills re-pull the product.
    /// </summary>
    public ManufacturingRole ManufacturingRole { get; set; }

    /// <summary>
    /// A raw material, consumable or semi-finished good that is sold as well (surplus sold to another
    /// workshop): the till offers it again and a customer's document may name it. Meaningless — and kept
    /// false — for a role the counter sells anyway. See <see cref="ManufacturingRoles.ForSale(Product)"/>.
    /// </summary>
    public bool AlsoForSale { get; set; }

    /// <summary>
    /// The unit stock, cost and every document line of this product are counted in, from the units
    /// table. Null until manufacturing sets it: <see cref="Unit"/> keeps its free text for every
    /// product, and setting a base unit writes the unit's code there, so receipts and screens keep
    /// printing the same word. Once set it changes only while nothing is held and no recipe names it,
    /// because a new unit would silently reinterpret every quantity already counted.
    /// </summary>
    public Guid? BaseUnitId { get; set; }
}

/// <summary>
/// Whether a customer is a natural person or a company — the owner's words: the hirer may be a
/// person, whose own details we take, or a company, which acts through an authorised
/// representative (المخوَّل) who deals with us and signs.
///
/// <para>
/// ⚠ Zero MUST stay <see cref="Unspecified"/>. Tests build the schema with
/// <c>EnsureCreatedAsync</c> (a new row gets the CLR default, 0) while production runs
/// <c>MigrateAsync</c> (an existing row gets the column default, 0); the two agree only while
/// the enum's zero is the value that means "we do not know". A test pins it.
/// </para>
/// </summary>
public enum CustomerKind
{
    /// <summary>A row that predates the distinction. The screen asks; the migration never guessed.</summary>
    Unspecified = 0,
    Person = 1,
    Company = 2
}

public class Customer : SyncEntity
{
    [MaxLength(32)] public string Code { get; set; } = string.Empty;
    /// <summary>
    /// The name on every document. For a company this is the legal name (raison sociale); for a
    /// person it is <see cref="FirstName"/> then <see cref="FamilyName"/>, composed on every save
    /// (owner, 2026-10-06: "الاسم واللقب" in two inputs, as the employee form has them).
    /// </summary>
    [MaxLength(200)] public string FullName { get; set; } = string.Empty;

    /// <summary>
    /// A person's first name. Null for a company and an unspecified row, and for a person filed
    /// before the two parts existed: their whole name was moved into <see cref="FamilyName"/>, so
    /// nothing printed changed, and the clerk splits it the next time the person is edited.
    /// </summary>
    [MaxLength(100)] public string? FirstName { get; set; }

    /// <summary>A person's family name (اللقب); for one filed before the parts existed, the whole name as typed.</summary>
    [MaxLength(200)] public string? FamilyName { get; set; }

    [MaxLength(60)] public string? Phone { get; set; }
    [MaxLength(400)] public string? Address { get; set; }
    /// <summary>The customer's tax number; its label comes from the country profile.</summary>
    [MaxLength(40)] public string? TaxId { get; set; }

    /// <summary>
    /// The customer's other fiscal identifiers — RC, AI, NIS in the Algeria preset — keyed by the
    /// country profile's identifier key, exactly as <see cref="Store.FiscalIds"/> holds the company's.
    /// A company's invoice prints them under the buyer (owner's Facture N° 45, 2026-10-10). Back
    /// office only, like the kind: <c>CustomerDto</c> does not carry it, so a till never sends it
    /// back and never overwrites it. Empty for a person, who has none.
    /// </summary>
    public Dictionary<string, string> FiscalIds { get; set; } = new();

    /// <summary>
    /// Person or company. Rows that predate the column are <see cref="CustomerKind.Unspecified"/>
    /// on purpose: guessing from the tax id or from "SARL" in the name would be right often enough
    /// to be trusted and wrong often enough to matter, so the customers screen says "kind not set"
    /// in words and asks. The migration never decides for the owner.
    /// </summary>
    public CustomerKind Kind { get; set; }

    /// <summary>
    /// A person's identity-document number — national id card, passport, driving licence.
    /// <b>Not a fiscal identifier:</b> its label is a plain i18n string, unlike <see cref="TaxId"/>,
    /// whose label comes from the country profile. Null for a company: the server clears it, and
    /// the company's representative carries their own.
    /// </summary>
    [MaxLength(40)] public string? IdDocumentNumber { get; set; }

    /// <summary>Which paper <see cref="IdDocumentNumber"/> is from: "nationalCard", "drivingLicence" or "passport".</summary>
    [MaxLength(40)] public string? IdDocumentType { get; set; }
    /// <summary>Who issued it — the wilaya, daïra or commune on the card.</summary>
    [MaxLength(120)] public string? IdDocumentIssuer { get; set; }
    public DateOnly? IdDocumentIssuedOn { get; set; }

    [MaxLength(200)] public string? Email { get; set; }
    /// <summary>Date part only; the time component is ignored.</summary>
    public DateTime? BirthDate { get; set; }
    public decimal CreditLimit { get; set; }
    public decimal Balance { get; set; }
    /// <summary>Server-owned, like <see cref="Balance"/>; tills keep an estimate until the next pull.</summary>
    public int LoyaltyPoints { get; set; }

    /// <summary>
    /// What the customer owed (positive) or was owed (negative) on the day the company started
    /// using the system (owner, 2026-10-10, go-live). Written <b>once</b> — at registration, or later
    /// by whoever holds <c>customers.opening_balance</c> when it was left empty — and never edited:
    /// a wrong figure is put right by a <see cref="CustomerOpeningCorrection"/> with its reason, so
    /// the statement keeps both. Zero with <see cref="OpeningBalanceSetAtUtc"/> null means not set.
    /// </summary>
    public decimal OpeningBalance { get; set; }

    /// <summary>The day the opening balance stands at, in the company's calendar.</summary>
    public DateOnly? OpeningBalanceOn { get; set; }

    /// <summary>When it was written; set means fixed. Null: never set, and still settable once.</summary>
    public DateTime? OpeningBalanceSetAtUtc { get; set; }

    [MaxLength(200)] public string? OpeningBalanceSetBy { get; set; }
}

/// <summary>
/// A correction of a customer's opening balance, with the reason written down (owner,
/// 2026-10-10: a wrong opening balance is corrected by an adjustment with a reason, never by
/// editing it). Posts 411 for that customer against opening-balance equity (890), exactly as the
/// opening balance did, and shows on the statement under its own number.
///
/// <para>
/// <b>Append-only and back-office only</b>, like <c>CreditNote</c>: never edited, never deleted,
/// not a <see cref="SyncEntity"/> — a till only sees the customer's balance move.
/// </para>
/// </summary>
public class CustomerOpeningCorrection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(32)] public string Number { get; set; } = string.Empty;

    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>The branch the entry is filed under: the caller's own, or the main branch for company scope.</summary>
    public Guid BranchId { get; set; }

    /// <summary>Signed: positive adds to what the customer owes, negative takes from it.</summary>
    public decimal Amount { get; set; }

    /// <summary>The day the correction is dated, in the company's calendar.</summary>
    public DateOnly EffectiveOn { get; set; }

    [MaxLength(400)] public string Reason { get; set; } = string.Empty;
    [MaxLength(200)] public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One person authorised to deal with the company and sign for a company customer, for a
/// period. A company's history of representatives is these rows in date order.
///
/// <para>
/// <b>Append-only.</b> A row is written once, and the only value ever set afterwards is
/// <see cref="ToUtc"/>, once, by the change that ends it. Nothing is edited and nothing is
/// deleted: a wrong entry is corrected by appointing again with a note. That is the point of a
/// history — the representative may leave the company, and the contract they signed keeps
/// their name (a snapshot on the contract, not a reference to this row).
/// </para>
/// <para>
/// <b>At most one current row per customer</b> (<see cref="ToUtc"/> null). Enforced in code
/// inside a write scope, not by a filtered unique index: a unique index over a nullable column
/// does not behave the same on SQLite and MySQL. The change ends every open row before it
/// inserts, and the reader takes "current" as <c>ToUtc == null</c> ordered by
/// <see cref="FromUtc"/> descending, first row — so a duplicate produced by a hand-run SQL fix is
/// at worst cosmetic.
/// </para>
/// <para>
/// <b>Back-office only, and deliberately not a <see cref="SyncEntity"/></b>, like
/// <c>Invoice</c>: nothing on a till reads who signs for a company, and the pull is driven by
/// the base type. The foreign key is Restrict rather than Cascade because customers are only
/// ever soft-deleted, and Restrict states that the history of who signed for a company is never
/// lost — the same choice <c>RentalContract</c>, <c>Invoice</c> and <c>ReceiptVoucher</c> make.
/// </para>
/// </summary>
public class CustomerRepresentative
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }

    [MaxLength(200)] public string FullName { get; set; } = string.Empty;

    /// <summary>Their own identity-document number, not the company's tax id.</summary>
    [MaxLength(40)] public string? IdDocumentNumber { get; set; }
    /// <summary>Which paper it is from: "nationalCard", "drivingLicence" or "passport".</summary>
    [MaxLength(40)] public string? IdDocumentType { get; set; }
    [MaxLength(120)] public string? IdDocumentIssuer { get; set; }
    public DateOnly? IdDocumentIssuedOn { get; set; }
    [MaxLength(60)] public string? Phone { get; set; }

    /// <summary>Role or remark — "gérant", "directeur technique", why the previous one left.</summary>
    [MaxLength(300)] public string? Note { get; set; }

    /// <summary>When this person took over.</summary>
    public DateTime FromUtc { get; set; }

    /// <summary>When they stopped. Null means current — the only value ever set on a row after it is written.</summary>
    public DateTime? ToUtc { get; set; }

    [MaxLength(200)] public string? CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A copy of a customer's identity document — the scan or the phone photo of the card, the
/// licence or the passport (owner, 2026-10-06: attached when the customer is added, optional,
/// and added later from the edit when it was not). Several per customer: the two sides of a
/// card are two files. The row keeps what the file is and where it is; the bytes are in the
/// files folder (owner, 2026-10-04), reached only through the customer's own signed-in route.
///
/// <para>
/// Back-office only and not a <see cref="SyncEntity"/>: nothing on a till reads a scan.
/// </para>
/// </summary>
public class CustomerAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>What the file is: "idDocument".</summary>
    [MaxLength(40)] public string Kind { get; set; } = string.Empty;
    [MaxLength(200)] public string FileName { get; set; } = string.Empty;
    [MaxLength(100)] public string ContentType { get; set; } = string.Empty;
    public long Length { get; set; }
    /// <summary>Where the file is kept in the files folder (<c>FilePaths.CustomerAttachment</c>).</summary>
    [MaxLength(200)] public string StoragePath { get; set; } = string.Empty;

    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(100)] public string? UploadedBy { get; set; }
}

/// <summary>
/// A place a customer takes hired goods to — the building site named on the contract and the
/// issue note. A contractor has several at once, so they are rows of their own rather than a
/// field on the customer; the contract snapshots the one it was signed for
/// (<c>RentalContract.SiteName</c> / <c>SiteAddress</c>) so a later rename never rewrites a
/// signed paper.
///
/// <para>
/// <b>Back-office only, and deliberately not a <see cref="SyncEntity"/></b>, like
/// <see cref="CustomerRepresentative"/>: a till never needs a customer's sites. Restrict on the
/// customer for the same reason the representative history is Restrict — customers are only ever
/// soft-deleted and a site a contract points at is never lost.
/// </para>
/// </summary>
public class CustomerSite
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }

    [MaxLength(120)] public string Name { get; set; } = string.Empty;
    [MaxLength(300)] public string? Address { get; set; }
    [MaxLength(40)] public string? Phone { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public enum PaymentMethod { Cash = 0, Card = 1, Credit = 2, Mixed = 3, GiftCard = 4, Loyalty = 5 }
public enum SaleStatus { Completed = 0, Refunded = 1 }

/// <summary>
/// Whether tax applies to this document. Chosen per invoice, because a customer may need a
/// taxed invoice or may not. An <see cref="Untaxed"/> document carries no tax line and no rate
/// breakdown, and is excluded from the tax report — a figure that must be right at the point
/// it is declared, so it is filtered at source rather than patched afterwards.
/// </summary>
public enum TaxMode { Taxed = 0, Untaxed = 1 }

/// <summary>
/// A completed sale. Append-only and immutable: the Id is generated by the terminal, so a
/// re-pushed sale is recognised as a duplicate rather than double-recorded.
/// </summary>
public class Sale
{
    public Guid Id { get; set; }
    [MaxLength(40)] public string Reference { get; set; } = string.Empty;
    [MaxLength(16)] public string DeviceCode { get; set; } = string.Empty;
    /// <summary>Resolved server-side from the selling device at push time — never client-supplied.</summary>
    public Guid BranchId { get; set; }
    public long LocalSeq { get; set; }

    /// <summary>Terminal clock at the moment of sale (may drift while offline).</summary>
    public DateTime SoldAtUtc { get; set; }
    /// <summary>Server clock when the sale was accepted — the gap from SoldAtUtc is the offline window.</summary>
    public DateTime ReceivedAtUtc { get; set; }

    public Guid? CustomerId { get; set; }
    public Customer? Customer { get; set; }
    public Guid CashierUserId { get; set; }
    [MaxLength(200)] public string CashierName { get; set; } = string.Empty;

    public decimal SubTotalHt { get; set; }
    public decimal TaxTotal { get; set; }
    public decimal DiscountTotal { get; set; }
    public decimal GrandTotalTtc { get; set; }
    /// <summary>
    /// Cash rounding applied at the till (amount due = GrandTotalTtc + this). Kept apart from the
    /// lines so the tax figures stay exact.
    /// </summary>
    public decimal RoundingAdjustment { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal ChangeAmount { get; set; }

    public PaymentMethod PaymentMethod { get; set; }
    public SaleStatus Status { get; set; }

    /// <summary>
    /// Taxed or not, decided when the document is raised. Defaults to Taxed so every existing
    /// row keeps its current meaning after the migration.
    /// </summary>
    public TaxMode TaxMode { get; set; } = TaxMode.Taxed;
    public Guid? RefundOfSaleId { get; set; }
    [MaxLength(500)] public string? Note { get; set; }

    /// <summary>Set when the server had to take stock negative to accept this sale.</summary>
    public bool StockWarning { get; set; }

    public List<SaleLine> Lines { get; set; } = new();
    public List<SalePayment> Payments { get; set; } = new();
}

public class SaleLine
{
    public Guid Id { get; set; }
    public Guid SaleId { get; set; }
    public Sale? Sale { get; set; }
    public Guid ProductId { get; set; }
    [MaxLength(64)] public string Sku { get; set; } = string.Empty;
    /// <summary>Product name captured at sale time so history survives catalog renames.</summary>
    [MaxLength(200)] public string NameSnapshot { get; set; } = string.Empty;
    public decimal Qty { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TaxRate { get; set; }
    public decimal LineDiscount { get; set; }
    public decimal LineTotalTtc { get; set; }
    public decimal LineTaxAmount { get; set; }

    /// <summary>
    /// The goods left stock with no cost behind them: the product is stock-tracked and its cost
    /// was 0 when the sale reached the server, so no cost of goods sold was posted for this line.
    /// A sale is never refused for it (owner, 2026-10-31, SAL-02) — the line is flagged instead,
    /// so the margin it makes is known to be incomplete and the product's cost can be corrected.
    /// A service (stock not tracked) has no cost by design and is never flagged.
    /// </summary>
    public bool CostMissing { get; set; }

    /// <summary>
    /// The cost per unit the sale's ledger entry posted for this line (P0.4a): the product's cost
    /// price when the sale reached the server, 0 when that was 0 (<see cref="CostMissing"/>), null
    /// for a service, whose stock is not tracked, and on lines written before 2026-11-08. It records
    /// the cost of sales actually posted, so a refund can later give back that cost (P0.4c) and the
    /// weighted-average shadow can be compared with it. Server-only: never sent to a till.
    /// <para>Since C-01 (2026-11-09), with sales costed at the weighted average, it is that average at the moment the
    /// sale reached the server — kept to 10 decimals — and <see cref="CostBasis"/> says where it came from.</para>
    /// </summary>
    public decimal? UnitCost { get; set; }

    /// <summary>What <see cref="UnitCost"/> was taken from; <see cref="SaleCostBasis.CostPrice"/> on every older line.</summary>
    public SaleCostBasis CostBasis { get; set; }
}

public class SalePayment
{
    public Guid Id { get; set; }
    public Guid SaleId { get; set; }
    public Sale? Sale { get; set; }
    /// <summary>
    /// Kept for the built-in tenders the rest of the code dispatches on. Operator-defined
    /// tenders land here as <see cref="PaymentMethod.Card"/>-neutral: read
    /// <see cref="Behavior"/> instead when deciding what a payment *does*.
    /// </summary>
    public PaymentMethod Method { get; set; }
    public decimal Amount { get; set; }
    [MaxLength(100)] public string? Reference { get; set; }

    /// <summary>
    /// Which <see cref="PaymentMethodDef"/> this was taken under. Stored rather than
    /// referenced so renaming or retiring a method never rewrites history — the same reason
    /// <see cref="SaleLine.NameSnapshot"/> exists.
    /// </summary>
    [MaxLength(40)] public string MethodKey { get; set; } = string.Empty;

    /// <summary>Frozen at push time from the method definition.</summary>
    public PaymentBehavior Behavior { get; set; }

    /// <summary>
    /// Cash is Confirmed immediately; anything the method marks as needing confirmation
    /// starts Pending and does not reduce the customer's balance until it clears.
    /// </summary>
    public PaymentStatus Status { get; set; } = PaymentStatus.Confirmed;

    public DateTime? ConfirmedAtUtc { get; set; }
    /// <summary>Why a cheque bounced, or why a pending payment was written off.</summary>
    [MaxLength(400)] public string? StatusNote { get; set; }

    // Instrument details. Columns rather than a JSON blob because "which cheques are due this
    // week" has to be a query, and a blob turns the most useful screen into a table scan.
    [MaxLength(60)] public string? InstrumentNumber { get; set; }
    [MaxLength(120)] public string? InstrumentBank { get; set; }
    public DateTime? InstrumentDueDate { get; set; }
}

public class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(16)] public string Code { get; set; } = string.Empty;
    [MaxLength(120)] public string Name { get; set; } = string.Empty;
    /// <summary>Which branch this till sells for. Drives which branch's stock a sale decrements.</summary>
    public Guid BranchId { get; set; }
    public Branch? Branch { get; set; }
    /// <summary>
    /// The cashier this till belongs to, or null for a shared counter till. Set when a cashier is
    /// created (owner, 2026-10-05: the till comes with the cashier), so they sign in online without
    /// typing a code (Infrastructure/Auth/CashierTills).
    /// </summary>
    public Guid? UserId { get; set; }
    public User? User { get; set; }
    public DateTime? LastSyncAtUtc { get; set; }
    public long LastCursor { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Single-row table holding the global monotonic version counter.</summary>
public class SyncCounter
{
    public int Id { get; set; } = 1;
    public long Value { get; set; }
}

/// <summary>Audit trail of every stock movement, so a branch's stock can always be recomputed.</summary>
public class StockMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    public Guid BranchId { get; set; }
    public decimal QtyDelta { get; set; }
    public decimal BalanceAfter { get; set; }

    /// <summary>
    /// How long a reason may be. A short audit label on purpose — it is read down a column of
    /// movements, not as prose — but the number has to be reachable by the endpoints that fill
    /// the field, because MySQL refuses an over-long value while SQLite silently accepts it.
    /// Without this constant the limit existed only here, dev and CI never met it, and
    /// production answered 500.
    /// </summary>
    public const int ReasonMaxLength = 32;

    [MaxLength(ReasonMaxLength)] public string Reason { get; set; } = "sale";
    /// <summary>Set on both rows of a transfer, so the pair can be found and displayed together.</summary>
    public Guid? RelatedBranchId { get; set; }
    public Guid? SaleId { get; set; }

    /// <summary>
    /// The document these lines belong to — the receiving note, opening balance, state change
    /// or transfer that produced them, and the <c>SourceId</c> of the ledger entry raised for
    /// it. Without it a receiving note of five lines is five unrelated rows and a figure in an
    /// accounting report cannot be traced back to the act that caused it.
    /// <para>Null on everything written before the ledger existed, which is correct: those
    /// movements belonged to no document.</para>
    /// </summary>
    public Guid? DocumentId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Who did it, and their name as it stood then, so a renamed or removed account still reads
    /// (owner, 2026-10-12). Filled by every writer from that day; null on older rows, which the
    /// item movement report shows as "—" rather than guess.
    /// </summary>
    public Guid? UserId { get; set; }
    [MaxLength(200)] public string? UserName { get; set; }

    /// <summary>
    /// The number printed on the document behind the movement — the receipt, the issue or return
    /// note, the receiving note's supplier reference. Null where the act has no number (an
    /// adjustment, a state change) and on rows written before 2026-10-12.
    /// </summary>
    [MaxLength(DocumentNumberMaxLength)] public string? DocumentNumber { get; set; }
    public const int DocumentNumberMaxLength = 64;

    /// <summary>The supplier a receiving note bought from; null on every other movement.</summary>
    public Guid? SupplierId { get; set; }

    /// <summary>
    /// The quantity the act moved. <see cref="QtyDelta"/> is the change in what the branch owns,
    /// so a state change or a rental issue — the same goods in another state — writes zero there
    /// and, without this, the report could not say how many went. Filled from 2026-10-12.
    /// </summary>
    public decimal? MovedQty { get; set; }

    /// <summary>
    /// What kind of act this was, as a value rather than a label (P0.4a). <see cref="Reason"/> stays
    /// the human label and may carry the operator's words — an adjustment's reason is whatever was
    /// typed — so it cannot be read back reliably. Every writer sets this from 2026-11-08; older rows
    /// were classified from their reason by the migration, and what could not be is
    /// <see cref="StockMovementKind.Unclassified"/>.
    /// </summary>
    public StockMovementKind Kind { get; set; }

    /// <summary>
    /// The cost per unit this movement was valued at when it happened (P0.4a): the reference cost
    /// the ledger entry beside it used — the receiving price, the product's cost price at a sale, an
    /// adjustment or a state change, the transfer line's cost, a production issue's snapshot. It
    /// records what was done and changes nothing that is done; the weighted-average shadow reads it
    /// to compare the two methods. Null on rows written before 2026-11-08 and where no entry values
    /// the act (a rental issue: the same goods, hired out).
    /// </summary>
    public decimal? UnitCost { get; set; }

    /// <summary>
    /// What <see cref="UnitCost"/> was taken from, on the stock documents that follow the sales-cost switch (C-01): an
    /// adjustment, a write-off, a state change, a transfer step, a rental return, an opening restated downwards.
    /// <see cref="SaleCostBasis.CostPrice"/> (today's rule) on every row written while sales are costed at the cost price,
    /// on every older row, and on the rows the switch does not reach (receipts, openings that raise a quantity — both come
    /// in at their own cost —, sales — whose line says it —, production);
    /// <see cref="SaleCostBasis.WeightedAverage"/> or <see cref="SaleCostBasis.CostPriceFallback"/> while it is on, so the
    /// Stock valuation page can count the rows that fell back.
    /// </summary>
    public SaleCostBasis CostBasis { get; set; }

    /// <summary>
    /// A label that certainly fits: a fixed prefix the history is read by, then whatever the
    /// operator typed, cut at the column's width rather than refused. Used where the caller's
    /// words decorate a prefix the system chose (<c>receiving: INV-2026-0041</c>,
    /// <c>available-&gt;maintenance: seal</c>); where the caller's words <i>are</i> the whole
    /// label, the endpoint says 400 and names the limit instead of quietly losing half of it.
    /// The full text is never lost either way — the ledger entry raised beside the movement
    /// carries it in a 400-character description.
    /// </summary>
    public static string Label(string prefix, string? detail)
    {
        var text = string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix}: {detail.Trim()}";
        return text.Length <= ReasonMaxLength ? text : text[..ReasonMaxLength];
    }
}

/// <summary>
/// The act behind a stock movement (P0.4a). Stored as its number, so the values are fixed: a new
/// kind takes a new number and none is ever reused.
/// </summary>
public enum StockMovementKind
{
    /// <summary>Written before kinds existed, with a reason the migration could not place.</summary>
    Unclassified = 0,
    /// <summary>An opening balance stated or restated; the delta is the difference.</summary>
    Opening = 1,
    /// <summary>A receiving note: goods bought in.</summary>
    Receipt = 2,
    Sale = 3,
    Refund = 4,
    /// <summary>A stock count or correction typed in the back office.</summary>
    Adjustment = 5,
    /// <summary>Goods moved between owned states (available, maintenance, scrap); no change in what is owned.</summary>
    StateChange = 6,
    /// <summary>Goods written off: they leave the business.</summary>
    WriteOff = 7,
    RentalIssue = 8,
    /// <summary>Goods back from a rental, into a state at the branch that hired them out.</summary>
    RentalReturn = 9,
    /// <summary>Goods back from a rental at another branch: one row out at the issuing branch, one in at the receiving one.</summary>
    RentalReturnTransfer = 10,
    TransferOut = 11,
    TransferIn = 12,
    /// <summary>A cancelled transfer's goods back at the source.</summary>
    TransferCancel = 13,
    /// <summary>Goods lost in transit: delta 0 (they left at dispatch), the moved quantity leaves the company.</summary>
    TransferLoss = 14,
    ManufacturingIssue = 15,
    ManufacturingReturn = 16,
    ManufacturingOutput = 17,
    /// <summary>
    /// Goods sold on an invoice typed in the back office (P0.3), out of the invoice's branch, once the accountant has
    /// switched direct invoices to take their goods out of stock (<see cref="StockValuationSettings.DirectInvoiceStock"/>).
    /// <see cref="StockMovement.DocumentId"/> is the invoice.
    /// </summary>
    InvoiceSale = 18,
    /// <summary>
    /// Goods a hand-made credit note brought back into stock, at the cost their invoice line took them out at (P0.3).
    /// <see cref="StockMovement.DocumentId"/> is the credit note.
    /// </summary>
    InvoiceReturn = 19,
    /// <summary>
    /// Parts taken out of a branch's stock to a maintenance job (Phase M2, docs/maintenance/plan.md §2.3). The plan named
    /// it 18; 18 and 19 went to the direct invoices first, so it takes the next free value.
    /// <see cref="StockMovement.DocumentId"/> is the issue document (its <c>MaintenanceJobPart</c> lines).
    /// </summary>
    MaintenanceIssue = 20,
    /// <summary>Parts a maintenance job did not use, back into its branch's stock at the cost they went out at (Phase M2).</summary>
    MaintenanceReturn = 21,
}

/// <summary>
/// Where the weighted-average shadow starts (P0.4b): what the company held at one instant and the
/// reference cost it was carried at, one row per product and place — a location (<see cref="BranchId"/>)
/// or an open transfer the goods were travelling on (<see cref="TransferId"/>). Taken once, by the
/// server at start-up, the first time it runs with movement costs (2026-11-08): movements before it
/// carry no cost, so the shadow cannot start any earlier. Read only by the shadow.
/// </summary>
public class StockValuationOpening
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductId { get; set; }
    /// <summary>The location holding the quantity; null on an in-transit row.</summary>
    public Guid? BranchId { get; set; }
    /// <summary>The open transfer the quantity was travelling on; null on a location row.</summary>
    public Guid? TransferId { get; set; }
    /// <summary>The instant: movements created after it are the shadow's; those at or before it are in the quantity.</summary>
    public DateTime TakenAtUtc { get; set; }
    /// <summary>A location's owned quantity (available, on rent, maintenance, scrap), or a transfer line's in-transit remainder.</summary>
    public decimal Qty { get; set; }
    /// <summary>The product's cost price at that instant: the only cost the system held.</summary>
    public decimal UnitCost { get; set; }
}

/// <summary>
/// The choices the weighted-average shadow makes that the accountant has not answered yet (owner,
/// 2026-11-08: "for the things that need a decision, make it settings and the admin can choose").
/// One row. The shadow is recomputed on every read, so changing a choice shows its result at once
/// and changes nothing stored, posted or sold.
/// </summary>
public class StockValuationSettings
{
    public const int SingletonId = 1;
    public int Id { get; set; } = SingletonId;
    /// <summary>C-01: one average per product for the company (suggested), or one per location.</summary>
    public StockValuationScope Scope { get; set; } = StockValuationScope.Company;
    /// <summary>P0.4c / COR-01: a refund's goods come back at the cost their sale took out (suggested), or at today's average.</summary>
    public RefundValuation RefundCost { get; set; } = RefundValuation.SaleCost;
    /// <summary>C-17: the decimals a unit cost is rounded to when goods go out (3 suggested); the value left absorbs the rest.</summary>
    public int UnitCostDecimals { get; set; } = 3;
    /// <summary>When the opening was taken (<see cref="StockValuationOpening"/>): the row's existence is what says it was, even with nothing in stock.</summary>
    public DateTime StartedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
    [MaxLength(200)] public string? UpdatedBy { get; set; }

    /// <summary>
    /// C-01, the switch itself (owner, 2026-11-09: every accounting decision is a setting the accountant sets in the
    /// panel; the default keeps today's behaviour). <see cref="SalesCostMethod.CostPrice"/>: a sale posts the
    /// product's cost price, as it always has. <see cref="SalesCostMethod.WeightedAverage"/>: a sale posts the
    /// shadow's average at the moment it reaches the server, and a refund comes back under <see cref="RefundCost"/>.
    /// Switched on only through the switch, which posts the opening adjustment (C-02) once; switched off, nothing
    /// is posted back. Each change is in <see cref="StockValuationSwitch"/>.
    /// </summary>
    public SalesCostMethod SalesCost { get; set; } = SalesCostMethod.CostPrice;
    /// <summary>When <see cref="SalesCost"/> last changed, and who changed it; null while it never has.</summary>
    public DateTime? SalesCostChangedAtUtc { get; set; }
    [MaxLength(200)] public string? SalesCostChangedBy { get; set; }

    /// <summary>
    /// P0.3, the accountant's switch (owner, 2026-11-09: every accounting decision is a setting; the default keeps today's
    /// behaviour). Off (the default): an invoice typed in the back office posts 411 / 70x / 4457 only, moves no stock and
    /// posts no cost, as it always has. On: each of its lines whose product tracks stock takes the goods out of the
    /// invoice's branch and posts Dr 600 / Cr 310 at the cost method in force (<see cref="SalesCost"/>), in the invoice's
    /// own entry; and a credit note made by hand may bring the goods of such a line back. Read when an invoice is issued:
    /// an invoice issued while it was off is never touched afterwards.
    /// </summary>
    public bool DirectInvoiceStock { get; set; }
    /// <summary>When <see cref="DirectInvoiceStock"/> last changed, and who changed it; null while it never has.</summary>
    public DateTime? DirectInvoiceStockChangedAtUtc { get; set; }
    [MaxLength(200)] public string? DirectInvoiceStockChangedBy { get; set; }

    /// <summary>
    /// B-F01 (owner, 2026-11-09: every accounting decision is a setting; the default keeps today's behaviour): what a
    /// receiving note does to the product's company-wide cost price. <see cref="ReceiptCostPriceRule.LastCost"/> (the
    /// default) is the rule since the first release. In every mode a stated cost of 0 never replaces a non-zero cost price
    /// (B-F06). Unlike the shadow's three choices it is not locked while sales are costed at the average: it changes no
    /// movement the replay reads, only the reference figure later documents start from.
    /// </summary>
    public ReceiptCostPriceRule ReceiptCostRule { get; set; } = ReceiptCostPriceRule.LastCost;

    /// <summary>
    /// C-16 (owner, 2026-11-09: every accounting decision is a setting; the default keeps today's behaviour): who posts a
    /// negative-stock gap that a later inflow covers while sales are costed at the average. <see cref="GapPostingMode.Accountant"/>
    /// (the default): the page lists it and the accountant posts it. <see cref="GapPostingMode.Automatic"/>: the inflow's own
    /// transaction posts it to the <c>valuation_difference</c> slot's account, once; while that slot is unmapped it stays
    /// listed for the accountant. Not locked while sales post the average: it changes no figure the replay reads.
    /// </summary>
    public GapPostingMode GapPosting { get; set; } = GapPostingMode.Accountant;

    /// <summary>
    /// B-F01, its open part (same rule): when the product's cost price changes — a receiving note under the last cost rule,
    /// or the product card — while sales are costed at the cost price, whether the stock on hand is revalued to it.
    /// <see cref="CostPriceRevaluationMode.None"/> (the default): never, as always. <see cref="CostPriceRevaluationMode.StockOnHand"/>:
    /// one entry, the change × the quantity held, Dr/Cr 310 against the <c>valuation_difference</c> slot's account. Saved
    /// only while that slot is mapped. Under the weighted average there is no cost-price valuation to revalue: nothing posts.
    /// </summary>
    public CostPriceRevaluationMode CostPriceRevaluation { get; set; } = CostPriceRevaluationMode.None;

    /// <summary>
    /// What a receiving line stated at cost 0 is valued at (same rule). <see cref="ZeroCostReceiptValue.Zero"/> (the default):
    /// at 0, a free delivery, as always. <see cref="ZeroCostReceiptValue.CostPrice"/>: at the product's cost price as it
    /// stands — the cost is unknown, not free. Either way a 0 never replaces a non-zero cost price (B-F06).
    /// </summary>
    public ZeroCostReceiptValue ZeroCostReceipt { get; set; } = ZeroCostReceiptValue.Zero;
}

/// <summary>Who posts a covered negative-stock gap (C-16). Stored as its number.</summary>
public enum GapPostingMode { Accountant = 0, Automatic = 1 }

/// <summary>Whether a change of the cost price revalues the stock on hand (B-F01). Stored as its number.</summary>
public enum CostPriceRevaluationMode { None = 0, StockOnHand = 1 }

/// <summary>What a receiving line stated at 0 is valued at. Stored as its number.</summary>
public enum ZeroCostReceiptValue { Zero = 0, CostPrice = 1 }

/// <summary>
/// When goods are received, the product's cost price becomes (B-F01). Stored as its number.
/// <list type="bullet">
/// <item><see cref="LastCost"/>: the cost the note states, as it always has.</item>
/// <item><see cref="WeightedAverage"/>: after the receipt, the shadow's weighted average for the product, company-wide —
/// the Stock valuation report's figure. When none can be computed, the last cost, and it is logged.</item>
/// <item><see cref="Unchanged"/>: the note never writes it; it is changed on the product's card only.</item>
/// </list>
/// </summary>
public enum ReceiptCostPriceRule { LastCost = 0, WeightedAverage = 1, Unchanged = 2 }

public enum StockValuationScope { Company = 0, Location = 1 }

public enum RefundValuation { SaleCost = 0, CurrentAverage = 1 }

/// <summary>How a sale's cost of goods sold is valued (C-01). Stored as its number.</summary>
public enum SalesCostMethod { CostPrice = 0, WeightedAverage = 1 }

/// <summary>
/// One change of how sales are costed (C-01), with the figures it was made on: the stock at the shadow's average,
/// the stock accounts' balance and the opening adjustment posted (C-02; zero when switching back, which posts
/// nothing). The rows together are the audit trail; the latest one is the method in force.
/// </summary>
public class StockValuationSwitch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public SalesCostMethod Method { get; set; }
    public DateTime AtUtc { get; set; }
    [MaxLength(200)] public string? By { get; set; }
    /// <summary>The shadow's choices the switch was made under: they are locked while sales are costed at the average.</summary>
    public StockValuationScope Scope { get; set; }
    public RefundValuation RefundCost { get; set; }
    public int UnitCostDecimals { get; set; }
    public decimal ShadowValue { get; set; }
    public decimal BookValue { get; set; }
    /// <summary>What was posted to the opening-adjustment account: shadow less book, to 3 decimals. 0 when switching back.</summary>
    public decimal Adjustment { get; set; }
    /// <summary>The ledger entry the adjustment posted as; null when nothing posted.</summary>
    public Guid? LedgerEntryId { get; set; }
}

/// <summary>What a sale line's <see cref="SaleLine.UnitCost"/> was taken from (C-01, P0.4c). Stored as its number.</summary>
public enum SaleCostBasis
{
    /// <summary>The product's cost price: today's rule, and every line written before the switch existed.</summary>
    CostPrice = 0,
    /// <summary>The weighted average at the moment the sale (or refund) reached the server.</summary>
    WeightedAverage = 1,
    /// <summary>
    /// The switch was on but no average could be computed — nothing came in since the opening, or the computation
    /// failed — so the line was costed at the cost price instead. The sale is never refused for it (rule 1).
    /// </summary>
    CostPriceFallback = 2,
    /// <summary>A refund given back at the cost its sale took out (P0.4c, the refund setting's suggested answer).</summary>
    SaleCost = 3,
}
