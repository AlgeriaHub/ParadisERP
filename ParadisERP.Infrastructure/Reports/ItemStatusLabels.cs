namespace ParadisERP.Infrastructure.Reports;

/// <summary>
/// Localised labels and vocabulary for the Item Movement &amp; Status report across Arabic, French and English.
/// </summary>
public sealed class ItemStatusLabels
{
    public required string Lang { get; init; }
    public required bool RightToLeft { get; init; }

    public required string Title { get; init; }
    public required string Reference { get; init; }
    public required string Item { get; init; }
    public required string Unit { get; init; }
    public required string ItemType { get; init; }
    public required string Rental { get; init; }
    public required string Sale { get; init; }
    public required string Total { get; init; }
    public required string Warehouses { get; init; }
    public required string Branches { get; init; }
    public required string Rented { get; init; }
    public required string Maintenance { get; init; }
    public required string Scrap { get; init; }
    public required string Available { get; init; }
    public required string ContractsStillOut { get; init; }
    public required string UnexplainedOnRent { get; init; }
    public required string LeftToIssue { get; init; }
    public required string OtherAvailable { get; init; }
    public required string Totals { get; init; }

    public required string AsOf { get; init; }
    public required string ScopeCompany { get; init; }
    public required string ScopeLocation { get; init; }
    public required string ScopeOwn { get; init; }

    public required string Location { get; init; }
    public required string Kind { get; init; }
    public required string Warehouse { get; init; }
    public required string Branch { get; init; }
    public required string Inactive { get; init; }

    public required string Contract { get; init; }
    public required string Customer { get; init; }
    public required string IssueBranch { get; init; }
    public required string Site { get; init; }
    public required string IssueNote { get; init; }
    public required string IssueDate { get; init; }
    public required string ExpectedReturn { get; init; }
    public required string Overdue { get; init; }
    public required string Qty { get; init; }

    public required string Date { get; init; }
    public required string Document { get; init; }
    public required string Type { get; init; }
    public required string Detail { get; init; }
    public required string From { get; init; }
    public required string To { get; init; }
    public required string Supplier { get; init; }
    public required string Moved { get; init; }
    public required string In { get; init; }
    public required string Out { get; init; }
    public required string Balance { get; init; }
    public required string User { get; init; }
    public required string OpeningBalance { get; init; }
    public required string ClosingBalance { get; init; }
    public required string Movements { get; init; }

    /// <summary>What "in transit" means and where it counts (owner, 2026-10-12).</summary>
    public required string InTransitNote { get; init; }
    public required string InTransit { get; init; }
    public required string Status { get; init; }
    public required string DispatchedOn { get; init; }
    public required string CountedHere { get; init; }
    public required string Yes { get; init; }
    public required string No { get; init; }
    public required IReadOnlyDictionary<string, string> TransferStatuses { get; init; }

    public required IReadOnlyDictionary<string, string> MovementTypes { get; init; }
    public required IReadOnlyDictionary<string, string> States { get; init; }

    public string MovementType(string type) => MovementTypes.GetValueOrDefault(type.ToLowerInvariant(), type);
    public string State(string state) => States.GetValueOrDefault(state.ToLowerInvariant(), state);
    public string TransferStatus(string status) => TransferStatuses.GetValueOrDefault(status, status);

    public string ScopeWord(string kind) => kind.ToLowerInvariant() switch
    {
        "company" => ScopeCompany,
        "location" => ScopeLocation,
        "own" => ScopeOwn,
        _ => kind,
    };

    public string Name(string ar, string fr, string en)
    {
        var choice = Lang switch
        {
            "ar" => ar,
            "en" => en,
            _ => fr,
        };
        if (string.IsNullOrWhiteSpace(choice)) choice = fr;
        if (string.IsNullOrWhiteSpace(choice)) choice = en;
        if (string.IsNullOrWhiteSpace(choice)) choice = ar;
        return choice ?? "";
    }

    public static ItemStatusLabels For(string? lang)
    {
        var normalized = (lang ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "ar" => Ar(),
            "en" => En(),
            _ => Fr(),
        };
    }

    private static ItemStatusLabels Ar() => new()
    {
        Lang = "ar",
        RightToLeft = true,
        Title = "تقرير حركة وحالة الأصناف",
        Reference = "المرجع",
        Item = "الصنف",
        Unit = "الوحدة",
        ItemType = "نوع الصنف",
        Rental = "تأجير",
        Sale = "بيع",
        Total = "الإجمالي",
        Warehouses = "المستودعات",
        Branches = "الفروع",
        Rented = "مؤجر",
        Maintenance = "صيانة",
        Scrap = "خردة",
        Available = "متاح",
        ContractsStillOut = "عقود قيد التأجير",
        UnexplainedOnRent = "تأجير غير مبرر",
        LeftToIssue = "متبقي للتسليم",
        OtherAvailable = "مواقع أخرى (متاح)",
        Totals = "الإجمالي",
        AsOf = "بتاريخ",
        ScopeCompany = "الشركة كاملة",
        ScopeLocation = "موقع محدد",
        ScopeOwn = "الفرع الخاص",
        Location = "الموقع",
        Kind = "النوع",
        Warehouse = "مستودع",
        Branch = "فرع",
        Inactive = "غير نشط",
        Contract = "العقد",
        Customer = "الزبون",
        IssueBranch = "فرع التسليم",
        Site = "الموقع / الورشة",
        IssueNote = "سند تسليم",
        IssueDate = "تاريخ التسليم",
        ExpectedReturn = "تاريخ الإرجاع المتوقع",
        Overdue = "متأخر",
        Qty = "الكمية",
        Date = "التاريخ",
        Document = "المستند",
        Type = "النوع",
        Detail = "التفاصيل",
        From = "من",
        To = "إلى",
        Supplier = "المورد",
        Moved = "الكمية المنقولة",
        In = "وارد",
        Out = "صادر",
        Balance = "الرصيد",
        User = "المستخدم",
        OpeningBalance = "الرصيد الافتتاحي",
        ClosingBalance = "الرصيد الختامي",
        Movements = "حركة الصنف",
        InTransitNote = "قيد النقل: ما أُرسل ولم يُستلم بعد، ويُحسب على الفرع المرسِل حتى استلامه.",
        InTransit = "قيد النقل",
        Status = "الحالة",
        DispatchedOn = "تاريخ الإرسال",
        CountedHere = "محسوب هنا",
        Yes = "نعم",
        No = "لا",
        TransferStatuses = new Dictionary<string, string>
        {
            ["Dispatched"] = "قيد النقل", ["PartlyReceived"] = "مستلم جزئيًا", ["Received"] = "مستلم", ["Cancelled"] = "ملغى",
        },
        MovementTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sale"] = "بيع",
            ["refund"] = "استرجاع",
            ["receiving"] = "استلام",
            ["opening"] = "رصيد افتتاحي",
            ["transfer"] = "تحويل",
            ["transfer-out"] = "إرسال تحويل",
            ["transfer-in"] = "استلام تحويل",
            ["transfer-cancel"] = "إلغاء تحويل",
            ["transfer-lost"] = "فقد أثناء النقل",
            ["rental-issue"] = "تسليم تأجير",
            ["rental-return"] = "إرجاع تأجير",
            ["rental-return-transfer"] = "تحويل إرجاع تأجير",
            ["state"] = "تغيير حالة",
            ["adjustment"] = "تسوية",
        },
        States = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["available"] = "متاح",
            ["maintenance"] = "صيانة",
            ["scrap"] = "خردة",
            ["writtenoff"] = "مشطوب",
            ["written off"] = "مشطوب",
            ["onrent"] = "مؤجر",
            ["on rent"] = "مؤجر",
        }
    };

    private static ItemStatusLabels Fr() => new()
    {
        Lang = "fr",
        RightToLeft = false,
        Title = "Mouvement et situation des articles",
        Reference = "Référence",
        Item = "Article",
        Unit = "Unité",
        ItemType = "Type d'article",
        Rental = "Location",
        Sale = "Vente",
        Total = "Total",
        Warehouses = "Dépôts",
        Branches = "Agences",
        Rented = "En location",
        Maintenance = "En maintenance",
        Scrap = "Rebut",
        Available = "Disponible",
        ContractsStillOut = "Contrats en cours",
        UnexplainedOnRent = "Location inexpliquée",
        LeftToIssue = "Restant à livrer",
        OtherAvailable = "Autres sites (disponible)",
        Totals = "Totaux",
        AsOf = "Au",
        ScopeCompany = "Toute l'entreprise",
        ScopeLocation = "Un site",
        ScopeOwn = "Propre agence",
        Location = "Site",
        Kind = "Type",
        Warehouse = "Dépôt",
        Branch = "Agence",
        Inactive = "Inactif",
        Contract = "Contrat",
        Customer = "Client",
        IssueBranch = "Agence d'émission",
        Site = "Chantier",
        IssueNote = "Bon de sortie",
        IssueDate = "Date d'émission",
        ExpectedReturn = "Retour prévu",
        Overdue = "En retard",
        Qty = "Qté",
        Date = "Date",
        Document = "Document",
        Type = "Type",
        Detail = "Détail",
        From = "De",
        To = "Vers",
        Supplier = "Fournisseur",
        Moved = "Déplacé",
        In = "Entrée",
        Out = "Sortie",
        Balance = "Solde",
        User = "Utilisateur",
        OpeningBalance = "Solde initial",
        ClosingBalance = "Solde de clôture",
        Movements = "Mouvements",
        InTransitNote = "En transit : expédié et pas encore reçu ; compté à l'emplacement qui l'a expédié jusqu'à sa réception.",
        InTransit = "En transit",
        Status = "État",
        DispatchedOn = "Expédié le",
        CountedHere = "Compté ici",
        Yes = "Oui",
        No = "Non",
        TransferStatuses = new Dictionary<string, string>
        {
            ["Dispatched"] = "En transit", ["PartlyReceived"] = "Reçu en partie", ["Received"] = "Reçu", ["Cancelled"] = "Annulé",
        },
        MovementTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sale"] = "Vente",
            ["refund"] = "Remboursement",
            ["receiving"] = "Réception",
            ["opening"] = "Solde initial",
            ["transfer"] = "Transfert",
            ["transfer-out"] = "Transfert expédié",
            ["transfer-in"] = "Transfert reçu",
            ["transfer-cancel"] = "Transfert annulé",
            ["transfer-lost"] = "Perte en transit",
            ["rental-issue"] = "Sortie location",
            ["rental-return"] = "Retour location",
            ["rental-return-transfer"] = "Transfert retour location",
            ["state"] = "Changement d'état",
            ["adjustment"] = "Ajustement",
        },
        States = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["available"] = "Disponible",
            ["maintenance"] = "Maintenance",
            ["scrap"] = "Rebut",
            ["writtenoff"] = "Sorti",
            ["written off"] = "Sorti",
            ["onrent"] = "En location",
            ["on rent"] = "En location",
        }
    };

    private static ItemStatusLabels En() => new()
    {
        Lang = "en",
        RightToLeft = false,
        Title = "Item movement & status",
        Reference = "Reference",
        Item = "Item",
        Unit = "Unit",
        ItemType = "Item type",
        Rental = "Rental",
        Sale = "Sale",
        Total = "Total",
        Warehouses = "Warehouses",
        Branches = "Branches",
        Rented = "On rent",
        Maintenance = "Maintenance",
        Scrap = "Scrap",
        Available = "Available",
        ContractsStillOut = "Contracts still out",
        UnexplainedOnRent = "Unexplained on rent",
        LeftToIssue = "Contracted not yet issued",
        OtherAvailable = "Other locations (available)",
        Totals = "Totals",
        AsOf = "As of",
        ScopeCompany = "Company",
        ScopeLocation = "Location",
        ScopeOwn = "Own branch",
        Location = "Location",
        Kind = "Kind",
        Warehouse = "Warehouse",
        Branch = "Branch",
        Inactive = "Inactive",
        Contract = "Contract",
        Customer = "Customer",
        IssueBranch = "Issuing branch",
        Site = "Site",
        IssueNote = "Issue note",
        IssueDate = "Issue date",
        ExpectedReturn = "Expected return",
        Overdue = "Overdue",
        Qty = "Qty",
        Date = "Date",
        Document = "Document",
        Type = "Type",
        Detail = "Detail",
        From = "From",
        To = "To",
        Supplier = "Supplier",
        Moved = "Moved",
        In = "In",
        Out = "Out",
        Balance = "Balance",
        User = "User",
        OpeningBalance = "Opening balance",
        ClosingBalance = "Closing balance",
        Movements = "Movements",
        InTransitNote = "In transit: dispatched and not yet received; counted at the location that dispatched it until it is received.",
        InTransit = "In transit",
        Status = "Status",
        DispatchedOn = "Dispatched on",
        CountedHere = "Counted here",
        Yes = "Yes",
        No = "No",
        TransferStatuses = new Dictionary<string, string>
        {
            ["Dispatched"] = "In transit", ["PartlyReceived"] = "Partly received", ["Received"] = "Received", ["Cancelled"] = "Cancelled",
        },
        MovementTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sale"] = "Sale",
            ["refund"] = "Refund",
            ["receiving"] = "Receiving",
            ["opening"] = "Opening",
            ["transfer"] = "Transfer",
            ["transfer-out"] = "Transfer dispatched",
            ["transfer-in"] = "Transfer received",
            ["transfer-cancel"] = "Transfer cancelled",
            ["transfer-lost"] = "Lost in transit",
            ["rental-issue"] = "Rental issue",
            ["rental-return"] = "Rental return",
            ["rental-return-transfer"] = "Rental return transfer",
            ["state"] = "State change",
            ["adjustment"] = "Adjustment",
        },
        States = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["available"] = "Available",
            ["maintenance"] = "Maintenance",
            ["scrap"] = "Scrap",
            ["writtenoff"] = "Written off",
            ["written off"] = "Written off",
            ["onrent"] = "On rent",
            ["on rent"] = "On rent",
        }
    };
}
