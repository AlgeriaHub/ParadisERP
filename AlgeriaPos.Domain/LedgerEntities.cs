using System.ComponentModel.DataAnnotations;

namespace AlgeriaPos.Domain;

/// <summary>
/// The five sides of the accounting equation. <see cref="Asset"/> and <see cref="Expense"/>
/// carry a debit balance, the rest a credit balance; that single fact is what turns a list of
/// entries into a trial balance, a P&amp;L and a balance sheet without storing any of them.
/// </summary>
public enum LedgerAccountType
{
    Asset = 0,
    Liability = 1,
    Income = 2,
    Expense = 3,
    Equity = 4
}

/// <summary>
/// A line in the chart of accounts.
///
/// <para>
/// Deliberately <b>not</b> a <see cref="SyncEntity"/>: the ledger is server-only and never
/// replicates to a till (design §5.9). A cashier neither needs nor should hold the company's
/// accounts, and a disconnected device cannot answer a question about them correctly anyway.
/// </para>
/// </summary>
public class LedgerAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// The account number an accountant reads. Unique, and the key every posting in code
    /// refers to — ids are generated per company, codes are stable across all of them.
    /// </summary>
    [MaxLength(16)] public string Code { get; set; } = string.Empty;

    [MaxLength(200)] public string NameAr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameFr { get; set; } = string.Empty;
    [MaxLength(200)] public string NameEn { get; set; } = string.Empty;

    public LedgerAccountType Type { get; set; }

    /// <summary>
    /// Code posts to this account by its <see cref="Code"/>. A system account can be renamed
    /// — an accountant may well call 530 something else — but never re-typed, re-coded or
    /// removed, because a module would then post into a hole or into the wrong side of the
    /// balance sheet.
    /// </summary>
    public bool IsSystem { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>
/// One balanced accounting document: a sale, a receiving note, a remittance, a write-off.
///
/// <para>
/// <b>Users never write these by hand.</b> This is an operational system that posts, not an
/// accounting package, so every entry carries <see cref="SourceType"/>/<see cref="SourceId"/>
/// back to the document that caused it and any figure in any report can be traced to the act
/// that produced it.
/// </para>
/// <para>
/// <b>Nothing is deleted.</b> A correction is a reversing entry (<see cref="IsReversal"/>),
/// exactly as <see cref="Sale"/> is append-only.
/// </para>
/// </summary>
public class LedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>When the business event happened — the till's clock for a sale, not the server's.</summary>
    public DateTime EntryDate { get; set; }

    /// <summary>When the server actually wrote it. The gap from <see cref="EntryDate"/> is the offline window.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The branch this document belongs to. A dimension on every entry, because the owner runs
    /// several branches with their own wage bills and cash boxes and wants profit per branch
    /// and consolidated. Individual lines may sit at another branch — see
    /// <see cref="LedgerEntryLine.BranchId"/>.
    /// </summary>
    public Guid BranchId { get; set; }

    [MaxLength(40)] public string SourceType { get; set; } = string.Empty;
    public Guid SourceId { get; set; }

    [MaxLength(400)] public string? Description { get; set; }

    public bool IsReversal { get; set; }
    /// <summary>Which entry this one cancels. Set only on a reversal.</summary>
    public Guid? ReversalOfEntryId { get; set; }

    /// <summary>
    /// Money or goods moving between two of the company's own branches.
    ///
    /// <para>
    /// <b>Consolidating is not adding the branches up.</b> A movement between branches is real
    /// at each branch and is nothing at all for the group, so it is marked here and dropped
    /// when the group figures are produced. Get it wrong and a stock transfer valued at cost
    /// is counted twice, which produces a group profit simply larger than reality — the single
    /// most common error in multi-branch reporting (design §5.10).
    /// </para>
    /// <para>
    /// Dropping a whole entry is safe precisely because every entry balances: both halves of
    /// an inter-branch movement sit in the same entry and therefore net to zero for the group.
    /// </para>
    /// </summary>
    public bool IsInterBranch { get; set; }

    /// <summary>The other branch of an inter-branch movement, so the pair can be shown together.</summary>
    public Guid? CounterpartBranchId { get; set; }

    /// <summary>Who or what raised it. A sale carries the cashier; a remittance the collector.</summary>
    [MaxLength(200)] public string? CreatedBy { get; set; }

    public List<LedgerEntryLine> Lines { get; set; } = new();
}

/// <summary>
/// One side of one entry. Exactly one of <see cref="Debit"/> and <see cref="Credit"/> is
/// non-zero, and both are always positive — a negative debit is a credit written badly, and
/// allowing it would make every report sum the wrong way round on the day someone did.
/// </summary>
public class LedgerEntryLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EntryId { get; set; }
    public LedgerEntry? Entry { get; set; }

    public Guid AccountId { get; set; }
    public LedgerAccount? Account { get; set; }

    /// <summary>
    /// Which branch this side belongs to. Normally the entry's branch; a remittance is the
    /// case that needs the distinction — cash leaves one branch's box and arrives in another's
    /// safe inside a single balanced document, and branch profitability has to see both.
    /// <para>Carried on the line rather than derived, so a per-branch report is an indexed
    /// filter on one table instead of a join plus a null-coalesce.</para>
    /// </summary>
    public Guid BranchId { get; set; }

    /// <summary>
    /// Which third party this side belongs to, on the accounts that have one — customers (411)
    /// and the paper they hand over (413).
    ///
    /// <para>
    /// Without it "account 411 for that customer" is not a question the ledger can answer, and
    /// a customer statement could only ever be a second set of figures assembled from documents
    /// and hoped to agree. Carried on the line rather than derived from the source document, for
    /// the same reason as <see cref="BranchId"/>: a statement is then one indexed read on one
    /// table, instead of a join per kind of document with a new hole in it every time a module
    /// learns to post against a customer.
    /// </para>
    /// <para>
    /// Null on every other account, and on a third-party line with nobody behind it — a till
    /// sale to a walk-in customer that did not add up lands in suspense with no one to bill.
    /// </para>
    /// </summary>
    public Guid? CustomerId { get; set; }

    /// <summary>
    /// Which supplier this side belongs to, on account 401 (owner, 2026-10-10: a basic suppliers
    /// module) — what makes "account 401 for that supplier" answerable, exactly as
    /// <see cref="CustomerId"/> does for 411. Null on every other line, and on a 401 line raised
    /// before suppliers existed or by a receiving note that named none.
    /// </summary>
    public Guid? SupplierId { get; set; }

    public decimal Debit { get; set; }
    public decimal Credit { get; set; }

    [MaxLength(300)] public string? Memo { get; set; }

    /// <summary>Debit minus credit: positive for an asset or expense that grew.</summary>
    public decimal Signed => Debit - Credit;
}

/// <summary>
/// Raised when a module tries to post something that does not balance.
///
/// <para>
/// This is loud on purpose. An unbalanced entry is a bug in whichever module produced it, and
/// the only moment it can be attributed to that module is the moment it is created; discovered
/// later, from a trial balance that does not add up, it is a forensic exercise across every
/// posting the system has ever made.
/// </para>
/// </summary>
public sealed class LedgerImbalanceException(string message) : InvalidOperationException(message);

/// <summary>
/// One close or reopen of the books (CLO-01; owner, 2026-10-31 and 2026-11-01). Months are closed after review
/// and nothing may be posted into a closed one; a reopen is the controlled way back, with its reason. The latest
/// row is in force; the rows together are the audit trail of who closed or reopened what, and when.
/// </summary>
public class BookClosing
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The last day closed, on the company's clock; null when a reopen opened every month.</summary>
    public DateOnly? ClosedThrough { get; set; }

    /// <summary>
    /// The first instant still open — midnight after <see cref="ClosedThrough"/> on the company's clock, as UTC — or
    /// null when nothing is closed. Stored so the ledger can compare an entry's date without a time zone.
    /// </summary>
    public DateTime? OpenFromUtc { get; set; }

    public bool IsReopen { get; set; }

    /// <summary>Why the books were reopened. Required for a reopen; a close may say why too.</summary>
    [MaxLength(400)] public string? Reason { get; set; }

    public Guid? UserId { get; set; }
    [MaxLength(200)] public string? UserName { get; set; }
    public DateTime AtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// An entry dated inside the closed books (CLO-01). Refused, never posted: the document that wanted it is not
/// saved. The Admin reopens the month, with a reason, to post into it.
/// </summary>
public sealed class PeriodClosedException(DateOnly closedThrough, string sourceType)
    : InvalidOperationException($"The books are closed through {closedThrough:yyyy-MM-dd}: nothing can be posted on or before that day ({sourceType}).")
{
    public DateOnly ClosedThrough { get; } = closedThrough;
    public string SourceType { get; } = sourceType;
}

/// <summary>
/// The account codes the system itself posts to, and the default chart seeded on every company.
///
/// <para>
/// The numbering follows the Algerian SCF / French PCG classes an accountant here already
/// reads — 1 equity, 3 inventory, 4 third parties, 5 cash and bank, 6 charges, 7 products —
/// so the output of this system can be handed to an accountant without a translation table.
/// Names are three-language and contain no rate, currency or fiscal label: those are the
/// country profile's, never a constant (see CLAUDE.md, "Country is configuration").
/// </para>
/// </summary>
public static class LedgerAccounts
{
    // ---------- equity ----------
    /// <summary>
    /// Where an opening balance lands. <b>Never a purchase and never income:</b> on the day a
    /// company adopts this system every branch already holds stock that was bought years ago.
    /// Post it as a purchase and the first period shows a cost that never occurred; post it as
    /// income and it shows a profit that never happened (design §5.3).
    /// </summary>
    public const string OpeningBalance = "890";

    /// <summary>
    /// What one branch owes another. A balance sheet for a single branch cannot balance
    /// without it: a remittance takes cash out of one branch and puts it into another, and the
    /// branch that lost it has to carry the claim somewhere. Across the group the two sides
    /// cancel, which is why it appears only on a single-branch sheet.
    /// </summary>
    public const string InterBranchCurrentAccount = "181";

    // ---------- inventory ----------
    public const string Inventory = "310";
    /// <summary>Damaged beyond use and awaiting sale as scrap. Still an asset, at a different value.</summary>
    public const string ScrapInventory = "318";

    // ---------- third parties ----------
    public const string Suppliers = "401";
    public const string Customers = "411";
    /// <summary>
    /// A cheque or transfer taken at the counter that has not cleared. Money promised on
    /// paper is not money, and keeping it apart from ordinary trade debt is what makes the
    /// "cheques due" screen answerable.
    /// </summary>
    public const string NotesReceivable = "413";
    public const string GiftCardLiability = "4181";
    public const string LoyaltyLiability = "4182";
    /// <summary>Built for renting and left switched off with the rest of the deposit feature (§5.6).</summary>
    public const string CustomerDeposits = "419";

    // ---------- payroll third parties (docs/07 §5.9) ----------
    // ⚠ The accountant confirms these eight codes before the first run posts. They follow the
    // classes this chart already uses, and once something has posted to one it cannot be recoded
    // — that is the whole reason they are named here rather than typed at a call site.

    /// <summary>What the company owes its staff for a month it has approved but not yet handed over.</summary>
    public const string WagesPayable = "421";

    /// <summary>Cash drawn against a wage and not yet taken back. An asset: the person owes it.</summary>
    public const string StaffAdvances = "425";

    /// <summary>
    /// Both halves of the social contribution — the employee's, withheld, and the employer's,
    /// carried as a cost. One account because one payment settles them.
    /// </summary>
    public const string SocialSecurityPayable = "431";

    /// <summary>Income tax withheld from wages and owed to the state until it is remitted.</summary>
    public const string IncomeTaxWithheld = "442";

    /// <summary>Employer levies on the wage bill, owed to whoever collects them.</summary>
    public const string OtherPayrollTaxesPayable = "447";

    public const string TaxCollected = "4457";
    /// <summary>
    /// The plug that keeps a sale from ever being refused. A sale that physically happened
    /// cannot be rejected because its tenders do not add up, so the difference lands here and
    /// shows on the trial balance as something to investigate, rather than blocking the till
    /// or silently distorting revenue.
    /// </summary>
    public const string Suspense = "471";

    // ---------- cash ----------
    public const string Cash = "530";
    public const string Bank = "512";

    // ---------- charges ----------
    public const string CostOfGoodsSold = "600";
    /// <summary>Maintenance the company pays for on its own assets. The customer-paid kind is income.</summary>
    public const string MaintenanceExpense = "615";

    /// <summary>
    /// The old staff-cost heading. Nothing posts to it: a wage bill that lands in one account
    /// cannot be read as wages, employer contributions and employer levies, which are three
    /// different questions the fund, the tax office and the owner each ask separately.
    /// <para>Kept seeded — a system account is never removed or recoded — and superseded by
    /// <see cref="Wages"/>, <see cref="EmployerSocialContributions"/> and
    /// <see cref="EmployerPayrollTaxes"/>.</para>
    /// </summary>
    [Obsolete("Heading; post to Wages (631) or EmployerSocialContributions (635).")]
    public const string PayrollExpense = "630";

    /// <summary>Gross wages, net of the deductions that genuinely reduce what the wage costs.</summary>
    public const string Wages = "631";

    /// <summary>The employer's own social contribution. A cost, never a deduction from anyone's pay.</summary>
    public const string EmployerSocialContributions = "635";

    /// <summary>Employer levies on wages. Also where a not-yet-confirmed "other" deduction lands (docs/07 §5.9).</summary>
    public const string EmployerPayrollTaxes = "641";

    public const string StockLoss = "657";

    // ---------- what a payment voucher spends on (the owner's answer 24, 2026-09-30) ----------
    // ⚠ The accountant confirms these four codes before the first expense voucher is paid. They
    // follow the SCF class 6 headings this chart already uses — 60 purchases not stocked, 62
    // external services, 65 other operating charges — and, like the payroll block, once one has
    // been posted to it cannot be recoded. A repair posts to 615, which already exists.

    /// <summary>Fuel and lubricants for the company's lorries and machines.</summary>
    public const string FuelExpense = "6061";

    /// <summary>Small purchases the branch does not stock: supplies, parts bought for use, consumables.</summary>
    public const string SundryPurchases = "6068";

    /// <summary>Carriage paid to someone else — a hired lorry, a delivery, a crane brought to site.</summary>
    public const string TransportExpense = "624";

    /// <summary>Any other expense the categories do not name. Kept apart so it can be looked at, not buried in one of them.</summary>
    public const string OtherOperatingExpense = "658";

    // ---------- products ----------
    public const string Sales = "700";
    public const string RentalIncome = "706";
    /// <summary>The same work as 615, posted the other way because the customer pays for it.</summary>
    public const string MaintenanceIncome = "707";
    /// <summary>Its own line, never merged into rent: the owner asked for it separately.</summary>
    public const string LateIncome = "708";
    public const string ScrapSales = "756";
    public const string DamageCompensation = "757";
    public const string RoundingDifference = "758";

    /// <summary>
    /// The chart every company starts with. Accounts a module posts to today are marked in the
    /// table below; the rest are seeded because design §5.1 names them as the lines the
    /// business needs, and an account that appears the day renting ships would otherwise have
    /// no history behind it.
    /// </summary>
    public static IReadOnlyList<LedgerAccount> Default { get; } =
    [
        A(OpeningBalance, LedgerAccountType.Equity, "الرصيد الافتتاحي", "Bilan d'ouverture", "Opening balance", 10),
        A(InterBranchCurrentAccount, LedgerAccountType.Equity, "حساب الربط بين الفروع", "Compte de liaison inter-unités", "Inter-branch current account", 11),

        A(Inventory, LedgerAccountType.Asset, "مخزون البضائع", "Stocks de marchandises", "Merchandise inventory", 20),
        A(ScrapInventory, LedgerAccountType.Asset, "مخزون الخردة", "Stocks au rebut", "Scrap inventory", 21),

        A(Suppliers, LedgerAccountType.Liability, "الموردون", "Fournisseurs", "Suppliers", 30),
        A(Customers, LedgerAccountType.Asset, "الزبائن", "Clients", "Customers", 31),
        A(NotesReceivable, LedgerAccountType.Asset, "أوراق القبض", "Clients — effets à recevoir", "Notes receivable", 32),
        A(GiftCardLiability, LedgerAccountType.Liability, "بطاقات الهدايا", "Cartes cadeaux", "Gift card liability", 33),
        A(LoyaltyLiability, LedgerAccountType.Liability, "نقاط الولاء", "Points de fidélité", "Loyalty liability", 34),
        A(CustomerDeposits, LedgerAccountType.Liability, "تسبيقات الزبائن", "Clients créditeurs — acomptes", "Customer deposits", 35),
        A(TaxCollected, LedgerAccountType.Liability, "الرسوم المحصلة على المبيعات", "Taxes collectées sur ventes", "Tax collected on sales", 36),
        A(Suspense, LedgerAccountType.Asset, "حساب انتظار", "Compte d'attente", "Suspense", 37),

        A(Bank, LedgerAccountType.Asset, "البنك", "Banque", "Bank", 40),
        A(Cash, LedgerAccountType.Asset, "الصندوق", "Caisse", "Cash on hand", 41),

        // The payroll block, appended rather than interleaved: an existing company's chart is
        // never renumbered by the seeder, so moving a sort order here would only make a new
        // company's screen differ from an upgraded one's. Every report orders by code anyway.
        A(WagesPayable, LedgerAccountType.Liability, "أجور مستحقة للمستخدمين", "Personnel — rémunérations dues", "Wages payable to staff", 45),
        A(StaffAdvances, LedgerAccountType.Asset, "تسبيقات للمستخدمين", "Personnel — avances et acomptes", "Advances to staff", 46),
        A(SocialSecurityPayable, LedgerAccountType.Liability, "الضمان الاجتماعي", "Organismes sociaux", "Social security payable", 47),
        A(IncomeTaxWithheld, LedgerAccountType.Liability, "ضرائب مقتطعة من المنبع", "État — impôts retenus à la source", "Income tax withheld payable", 48),
        A(OtherPayrollTaxesPayable, LedgerAccountType.Liability, "ضرائب ورسوم أخرى مستحقة", "État — autres impôts et taxes", "Other payroll taxes payable", 49),

        A(CostOfGoodsSold, LedgerAccountType.Expense, "تكلفة البضاعة المباعة", "Coût des marchandises vendues", "Cost of goods sold", 50),
        A(MaintenanceExpense, LedgerAccountType.Expense, "الصيانة والإصلاح", "Entretien et réparations", "Maintenance and repairs", 51),
#pragma warning disable CS0618 // Seeded on purpose: a system account is never removed, only left with nothing posting to it.
        A(PayrollExpense, LedgerAccountType.Expense, "أعباء المستخدمين", "Charges de personnel", "Payroll", 52),
#pragma warning restore CS0618
        A(StockLoss, LedgerAccountType.Expense, "خسائر المخزون", "Pertes sur stocks", "Stock losses and write-offs", 53),
        A(Wages, LedgerAccountType.Expense, "أجور المستخدمين", "Rémunérations du personnel", "Wages and salaries", 54),
        A(EmployerSocialContributions, LedgerAccountType.Expense, "اشتراكات الهيئات الاجتماعية", "Cotisations aux organismes sociaux", "Employer social contributions", 55),
        A(EmployerPayrollTaxes, LedgerAccountType.Expense, "ضرائب ورسوم على الأجور", "Impôts et taxes sur rémunérations", "Employer taxes on wages", 56),

        // The payment voucher's expense block, appended for the reason the payroll block is.
        A(FuelExpense, LedgerAccountType.Expense, "الوقود والزيوت", "Carburants et lubrifiants", "Fuel and lubricants", 57),
        A(SundryPurchases, LedgerAccountType.Expense, "مشتريات غير مخزنة", "Achats non stockés divers", "Sundry purchases not stocked", 58),
        A(TransportExpense, LedgerAccountType.Expense, "النقل", "Transports de biens", "Transport and carriage", 59),
        A(OtherOperatingExpense, LedgerAccountType.Expense, "أعباء تسيير أخرى", "Autres charges de gestion courante", "Other operating expenses", 67),

        A(Sales, LedgerAccountType.Income, "مبيعات البضائع", "Ventes de marchandises", "Merchandise sales", 60),
        A(RentalIncome, LedgerAccountType.Income, "إيرادات الإيجار", "Revenus de location", "Rental income", 61),
        A(MaintenanceIncome, LedgerAccountType.Income, "صيانة على حساب الزبون", "Maintenance refacturée", "Maintenance charged to customer", 62),
        A(LateIncome, LedgerAccountType.Income, "إيرادات التأخير", "Pénalités de retard", "Late-return income", 63),
        A(ScrapSales, LedgerAccountType.Income, "بيع الخردة", "Ventes de rebuts", "Scrap sales", 64),
        A(DamageCompensation, LedgerAccountType.Income, "تعويضات التلف والفقدان", "Indemnités de dommages", "Damage and loss compensation", 65),
        A(RoundingDifference, LedgerAccountType.Income, "فروق التقريب", "Écarts d'arrondi", "Rounding differences", 66)
    ];

    private static LedgerAccount A(string code, LedgerAccountType type, string ar, string fr, string en, int sort) =>
        new() { Code = code, Type = type, NameAr = ar, NameFr = fr, NameEn = en, IsSystem = true, SortOrder = sort };

    /// <summary>Which asset account holds a quantity sitting in a given stock state.</summary>
    /// <remarks>
    /// Available, on-rent and in-maintenance stock is all still merchandise the company owns,
    /// so it stays in one account and the quantity buckets carry the distinction. Scrap is a
    /// different asset. A write-off is not an asset at all — that is the whole difference
    /// between a loss and a state.
    /// </remarks>
    public static string ForStockState(StockState state) => state switch
    {
        StockState.Scrap => ScrapInventory,
        StockState.WrittenOff => StockLoss,
        _ => Inventory
    };
}

/// <summary>
/// The documents that post. Constants rather than free strings because
/// <c>sourceType</c>/<c>sourceId</c> is how a report drills back to its document, and a typo
/// would quietly break that trail for a whole class of entries.
/// </summary>
public static class LedgerSources
{
    public const string RentalIssueDailyCorrection = "correction.rental-issue-daily";
    public const string RentalReturnDailyCorrection = "correction.rental-return-daily";
    public const string RentalCreditDailyCorrection = "correction.rental-credit-daily";

    public static string? DailyCorrectionFor(string source) => source switch
    {
        RentalIssue => RentalIssueDailyCorrection,
        RentalReturn => RentalReturnDailyCorrection,
        CreditNote => RentalCreditDailyCorrection,
        _ => null
    };
    /// <summary>A pushed sale or refund. The id is the terminal's, so a re-push posts once.</summary>
    public const string Sale = "sale";
    /// <summary>
    /// Cash taken for a gift card — sold or topped up (SAL-17): owed to the customer until spent. The id is
    /// the card movement's, so each sale or top-up posts once.
    /// </summary>
    public const string GiftCardSale = "gift-card-sale";
    /// <summary>Part of a gift card's balance handed back in cash (SAL-17). The id is the card movement's.</summary>
    public const string GiftCardCashOut = "gift-card-cash-out";
    public const string Receiving = "receiving";
    public const string OpeningBalance = "opening";
    /// <summary>
    /// A customer's balance on the day the company started using the system. The id is the
    /// customer's own, so it posts once per customer however it is asked for (owner, 2026-10-10).
    /// </summary>
    public const string CustomerOpening = "customer-opening";
    /// <summary>A correction of a customer's opening balance, with its reason (<c>CustomerOpeningCorrection</c>).</summary>
    public const string CustomerOpeningCorrection = "customer-opening-correction";
    /// <summary>A supplier's balance on the day the company started on the system; the id is the supplier's own.</summary>
    public const string SupplierOpening = "supplier-opening";
    /// <summary>A correction of a supplier's opening balance, with its reason (<c>SupplierOpeningCorrection</c>).</summary>
    public const string SupplierOpeningCorrection = "supplier-opening-correction";
    /// <summary>A quantity moving between states — to maintenance, to scrap, or written off.</summary>
    public const string StockState = "stock-state";
    public const string StockTransfer = "stock-transfer";
    /// <summary>Goods declared lost in transit, written off at the branch that dispatched them.</summary>
    public const string StockTransferLoss = "stock-transfer-loss";
    /// <summary>A stocktake, breakage or correction. Posts, or the inventory account drifts from the count.</summary>
    public const string StockAdjustment = "stock-adjustment";
    public const string Remittance = "remittance";
    /// <summary>A cheque or transfer clearing, or failing to.</summary>
    public const string PaymentSettlement = "payment-settlement";

    /// <summary>
    /// Goods leaving on a rental. This is where the money starts: the full agreed period is
    /// invoiced when the goods go out, so the issue note — not the return — is what raises the
    /// rent (§5.5, confirmed 2026-09-23).
    /// </summary>
    public const string RentalIssue = "rental-issue";

    /// <summary>
    /// Goods coming back, and everything the condition they came back in costs: overdue days,
    /// repair charged on to the customer, compensation for what will not come back, and the
    /// unused part of the period given back.
    /// </summary>
    public const string RentalReturn = "rental-return";

    /// <summary>
    /// An invoice raised directly in the back office.
    ///
    /// <para>
    /// Only a direct one appears here. An invoice built over a sale or a rental note bills money
    /// those documents already posted, and posting it again under a second source type is the
    /// one way this module could report the same revenue twice.
    /// </para>
    /// </summary>
    public const string Invoice = "invoice";

    /// <summary>Money received from a customer against their account — the moment it is confirmed, never before.</summary>
    public const string CustomerReceipt = "customer-receipt";

    /// <summary>An invoice reversed, in whole or in part. The only correction there is.</summary>
    public const string CreditNote = "credit-note";

    /// <summary>
    /// Part of a customer advance held on 419 applied to an invoice or released to the customer's account (M7b): Dr 419,
    /// Dr 4457 for the advance's VAT released, Cr 411. The id is the <c>CustomerAdvanceApplication</c>'s.
    /// </summary>
    public const string CustomerAdvanceApplication = "customer-advance-application";

    /// <summary>
    /// Part of a customer's advance kept on a cancelled order (SAL-13), moved to the income account the accountant chose:
    /// Dr 419 (its VAT part moved off the customer on 4457, still due) or Dr 411 / Cr that income account. The id is the
    /// <c>CustomerAdvanceApplication</c>'s (kind Kept).
    /// </summary>
    public const string CustomerAdvanceKept = "customer-advance-kept";

    /// <summary>
    /// A month's wages accrued for one branch, at the moment the run is approved. One entry per
    /// branch per run — never one per employee: nine hundred entries a month would bury every
    /// other document in the journal and say nothing the run's own lines do not already say.
    /// </summary>
    public const string PayrollRun = "payroll-run";

    /// <summary>Wages actually handed over against an approved run.</summary>
    public const string WagePayment = "wage-payment";

    /// <summary>Cash drawn against a wage before the month closes.</summary>
    public const string WageAdvance = "wage-advance";

    /// <summary>The social fund or the tax office actually being paid what the run accrued.</summary>
    public const string PayrollStatutoryPayment = "payroll-statutory-payment";

    /// <summary>
    /// Money actually leaving under a payment voucher — a refund to a customer or an expense —
    /// at the moment it is paid, never when it is asked for or approved.
    /// </summary>
    public const string PaymentVoucher = "payment-voucher";

    /// <summary>
    /// Sales switched to the weighted average (C-01): the one entry of the opening adjustment (C-02), stock at the
    /// shadow's average less the stock accounts. The id is the switch's own row.
    /// </summary>
    public const string ValuationSwitch = "valuation-switch";

    /// <summary>
    /// REN-12: hire equipment moved out of 310 into the fixed-asset register, one entry per switch-over (Phase M7a). The id
    /// is the switch-over's own.
    /// </summary>
    public const string FixedAssetSwitch = "fixed-asset-switch";

    /// <summary>
    /// REN-12: one event of the fixed-asset register posted by a stock document — units disposed of, wrecked into scrap,
    /// moved between branches, found, or put back into service. The id is the register movement's own.
    /// </summary>
    public const string FixedAsset = "fixed-asset";

    /// <summary>A month's depreciation, posted by the accountant from the register's practice ledger.</summary>
    public const string FixedAssetDepreciation = "fixed-assets";

    /// <summary>
    /// A negative-stock gap settled (C-16): goods sold below zero at the average, covered by a later inflow at
    /// another cost. The id is the covering stock movement's, so each gap posts once.
    /// </summary>
    public const string ValuationGap = "valuation-gap";

    /// <summary>
    /// The stock on hand revalued to a changed cost price (B-F01, the accountant's setting): the change × the quantity
    /// held, Dr/Cr 310 against the valuation-difference account. The id is the receiving note's, or a fresh one for a
    /// change typed on the product card.
    /// </summary>
    public const string CostPriceRevaluation = "cost-price-revaluation";
}
