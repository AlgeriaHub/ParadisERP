using System.Globalization;
using ParadisERP.Application.Contracts;
using ParadisERP.Infrastructure.Reports;

namespace ParadisERP.Infrastructure.Rentals;

/// <summary>
/// The customer quantity statement as an Excel workbook: the same statement the screen shows,
/// written through the shared <see cref="XlsxWriter"/> — items (with the summary as the totals
/// row), movements, and contracts. Money is a number with the currency's decimals and no currency
/// sign: the currency is the company's configuration, not this file's.
/// </summary>
public static class QuantityStatementWorkbook
{
    public static byte[] Build(QuantityStatementDto s, string? lang, Func<Guid?, string> branchName, int moneyDecimals)
    {
        var words = Words.For(lang);
        var rtl = words == Words.Ar;
        var qtyDecimals = HasFraction(s) ? 3 : 0;
        var title = Title(s, words);

        var items = Items(s, words, title, rtl, qtyDecimals, moneyDecimals);
        var movements = new XlsxSheet
        {
            Name = words["sheet.movements"],
            RightToLeft = rtl,
            Title = title,
            Columns =
            [
                new(words["col.sku"]), new(words["col.item"]), new(words["col.date"], XlsxKind.Date),
                new(words["col.document"]), new(words["col.contract"]), new(words["col.movement"]),
                new(words["col.qty"], XlsxKind.Number, qtyDecimals), new(words["col.rate"], XlsxKind.Number, moneyDecimals),
                new(words["col.balance"], XlsxKind.Number, qtyDecimals), new(words["col.branch"]), new(words["col.site"]),
            ],
            Rows = s.Items.SelectMany(i => i.Movements.Select(m => (IReadOnlyList<object?>)
            [
                i.Sku, Name(i.Name, i.NameAr, rtl), m.On, m.DocumentNumber, m.ContractNumber, words["move." + m.Kind],
                m.Kind == "opening" ? null : m.Qty, m.UnitPrice, m.Balance,
                m.BranchId is null ? null : branchName(m.BranchId), m.SiteName,
            ])).ToList(),
        };

        var contractRows = s.Contracts.SelectMany(c => c.Lines.Select(l => (IReadOnlyList<object?>)
        [
            c.Number, words["status." + c.Status], branchName(c.BranchId), c.SiteName, l.FirstIssueOn,
            l.Sku, Name(l.Name, l.NameAr, rtl), l.Issued, l.Returned, l.MissingDamaged, l.TransferredIn, l.TransferredOut,
            l.Remaining, l.NoRent ? words["noRent"] : Money(l.RentUnitPrice, moneyDecimals), l.Value, l.ProvisionalValue,
        ])).ToList();
        var lines = s.Contracts.SelectMany(c => c.Lines).ToList();
        var contracts = new XlsxSheet
        {
            Name = words["sheet.contracts"],
            RightToLeft = rtl,
            Title = title,
            Columns =
            [
                new(words["col.contract"]), new(words["col.status"]), new(words["col.branch"]), new(words["col.site"]),
                new(words["col.issueDate"], XlsxKind.Date), new(words["col.sku"]), new(words["col.item"]),
                new(words["col.issued"], XlsxKind.Number, qtyDecimals), new(words["col.returned"], XlsxKind.Number, qtyDecimals),
                new(words["col.missingDamaged"], XlsxKind.Number, qtyDecimals),
                new(words["col.transferIn"], XlsxKind.Number, qtyDecimals), new(words["col.transferOut"], XlsxKind.Number, qtyDecimals),
                new(words["col.remaining"], XlsxKind.Number, qtyDecimals), new(words["col.rate"]),
                new(words["col.value"], XlsxKind.Number, moneyDecimals), new(words["provisional"], XlsxKind.Number, moneyDecimals),
            ],
            Rows = contractRows,
            Totals =
            [
                words["total"], null, null, null, null, null, null,
                lines.Sum(l => l.Issued), lines.Sum(l => l.Returned), lines.Sum(l => l.MissingDamaged),
                lines.Sum(l => l.TransferredIn), lines.Sum(l => l.TransferredOut), lines.Sum(l => l.Remaining), null,
                lines.Sum(l => l.Value), lines.Sum(l => l.ProvisionalValue),
            ],
        };

        return XlsxWriter.Write(items, movements, contracts);
    }

    private static XlsxSheet Items(
        QuantityStatementDto s, IReadOnlyDictionary<string, string> words, IReadOnlyList<string> title, bool rtl,
        int qtyDecimals, int moneyDecimals)
    {
        if (s.Current)
            return new XlsxSheet
            {
                Name = words["sheet.items"],
                RightToLeft = rtl,
                Title = title,
                Columns = [new(words["col.sku"]), new(words["col.item"]), new(words["col.remaining"], XlsxKind.Number, qtyDecimals)],
                Rows = s.Items.Select(i => (IReadOnlyList<object?>)[i.Sku, Name(i.Name, i.NameAr, rtl), i.Remaining]).ToList(),
                Totals = [words["total"], null, s.Summary.WithCustomer],
            };

        return new XlsxSheet
        {
            Name = words["sheet.items"],
            RightToLeft = rtl,
            Title = title,
            Columns =
            [
                new(words["col.sku"]), new(words["col.item"]),
                new(words["col.opening"], XlsxKind.Number, qtyDecimals), new(words["col.issued"], XlsxKind.Number, qtyDecimals),
                new(words["col.returned"], XlsxKind.Number, qtyDecimals), new(words["col.missingDamaged"], XlsxKind.Number, qtyDecimals),
                new(words["col.transfers"], XlsxKind.Number, qtyDecimals), new(words["col.remaining"], XlsxKind.Number, qtyDecimals),
                new(words["col.rate"]), new(words["col.value"], XlsxKind.Number, moneyDecimals),
                new(words["provisional"], XlsxKind.Number, moneyDecimals),
            ],
            Rows = s.Items.Select(i => (IReadOnlyList<object?>)
            [
                i.Sku, Name(i.Name, i.NameAr, rtl), i.Opening, i.Issued, i.Returned, i.MissingDamaged, i.TransferNet,
                i.Remaining, Rate(i, words, moneyDecimals), i.Value, i.ProvisionalValue,
            ]).ToList(),
            // The summary the screen shows, as it is — not a second sum.
            Totals =
            [
                words["total"], null, s.Items.Sum(i => i.Opening), s.Summary.Issued, s.Summary.Returned,
                s.Summary.MissingDamaged, s.Items.Sum(i => i.TransferNet), s.Summary.WithCustomer, null,
                s.Summary.Value, s.Summary.ProvisionalValue,
            ],
        };
    }

    private static List<string> Title(QuantityStatementDto s, IReadOnlyDictionary<string, string> words)
    {
        var title = new List<string>
        {
            words["title"],
            $"{s.CustomerCode} — {s.CustomerName}",
            s.Current
                ? string.Format(CultureInfo.InvariantCulture, words["asOf"], Day(s.To))
                : string.Format(CultureInfo.InvariantCulture, words["period"], Day(s.From), Day(s.To)),
            s.FirstIssueOn is DateOnly first
                ? string.Format(CultureInfo.InvariantCulture, words["notRecorded"], Day(first))
                : words["noIssue"],
        };
        if (s.Summary.ProvisionalValue > 0) title.Add(words["provisionalNote"]);
        foreach (var w in s.Warnings)
            title.Add(string.Format(CultureInfo.InvariantCulture, words["warn." + w.Code],
                w.On is DateOnly on ? Day(on) : "", w.ContractNumber ?? "", w.Qty?.ToString(CultureInfo.InvariantCulture) ?? ""));
        return title;
    }

    private static string Rate(QuantityStatementItemDto item, IReadOnlyDictionary<string, string> words, int decimals) =>
        item.NoRent ? words["noRent"]
        : item.Rates.Count == 1 ? Money(item.Rates[0].UnitPrice, decimals)
        : item.Rates.Count > 1 ? words["multipleRates"]
        : "";

    private static string Money(decimal value, int decimals) =>
        value.ToString("N" + Math.Clamp(decimals, 0, 6), CultureInfo.InvariantCulture);

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Name(string name, string? nameAr, bool rtl) => rtl && !string.IsNullOrWhiteSpace(nameAr) ? nameAr : name;

    private static bool HasFraction(QuantityStatementDto s) =>
        s.Items.Any(i => new[] { i.Opening, i.Issued, i.Returned, i.MissingDamaged, i.Remaining, i.TransferNet }.Any(q => q != decimal.Truncate(q)));

    /// <summary>The screen's own wording (translations-quantity-statement.ts), for the three built-in languages.</summary>
    private static class Words
    {
        public static IReadOnlyDictionary<string, string> For(string? lang) => lang switch
        {
            "ar" => Ar,
            "en" => En,
            _ => Fr,
        };

        public static readonly IReadOnlyDictionary<string, string> Fr = new Dictionary<string, string>
        {
            ["title"] = "Relevé des quantités client",
            ["sheet.items"] = "Par article",
            ["sheet.movements"] = "Mouvements",
            ["sheet.contracts"] = "Par contrat",
            ["period"] = "Période du {0} au {1}",
            ["asOf"] = "Situation au {0}",
            ["notRecorded"] = "Les quantités antérieures au premier bon de livraison ({0}) ne sont pas enregistrées : les contrats d’avant le système ont été repris comme un solde en argent seulement.",
            ["noIssue"] = "Aucun bon de livraison enregistré pour ce client.",
            ["provisionalNote"] = "Le matériel encore sorti est valorisé jusqu’à la date de fin aux tarifs du contrat ; ce montant évoluera à son retour.",
            ["warn.negativeBalance"] = "La quantité détenue est passée sous zéro le {0} sur le contrat {1} — vérifiez les dates saisies sur les bons.",
            ["warn.overReturned"] = "Un retour sur le contrat {1} déclare {2} de plus que ce qui a été livré.",
            ["warn.pricingCorrectionPending"] = "Le contrat {1} est encore en cours de conversion des prix mensuels en prix journaliers ; sa valeur peut changer.",
            ["col.sku"] = "Référence",
            ["col.item"] = "Article",
            ["col.opening"] = "Ouverture",
            ["col.issued"] = "Livré",
            ["col.returned"] = "Retourné",
            ["col.missingDamaged"] = "Manquant / détruit",
            ["col.transfers"] = "Transfert entre contrats",
            ["col.transferIn"] = "Transfert reçu",
            ["col.transferOut"] = "Transfert émis",
            ["col.remaining"] = "Chez le client",
            ["col.rate"] = "Prix unitaire / jour",
            ["col.value"] = "Valeur de location de la période",
            ["col.date"] = "Date",
            ["col.document"] = "Document",
            ["col.contract"] = "Contrat",
            ["col.movement"] = "Mouvement",
            ["col.qty"] = "Quantité",
            ["col.balance"] = "Solde",
            ["col.branch"] = "Succursale",
            ["col.site"] = "Chantier",
            ["col.issueDate"] = "Date de livraison",
            ["col.status"] = "État",
            ["provisional"] = "dont provisoire",
            ["multipleRates"] = "Plusieurs tarifs",
            ["noRent"] = "Sans loyer",
            ["total"] = "Total",
            ["status.open"] = "En cours",
            ["status.goodsBack"] = "Matériel rendu",
            ["status.closed"] = "Clôturé",
            ["move.opening"] = "Ouverture",
            ["move.issue"] = "Bon de livraison",
            ["move.return"] = "Retour",
            ["move.partialReturn"] = "Retour partiel",
            ["move.transferIn"] = "Transfert reçu",
            ["move.transferOut"] = "Transfert émis",
        };

        public static readonly IReadOnlyDictionary<string, string> Ar = new Dictionary<string, string>
        {
            ["title"] = "كشف كميات العميل",
            ["sheet.items"] = "حسب الصنف",
            ["sheet.movements"] = "الحركات",
            ["sheet.contracts"] = "حسب العقد",
            ["period"] = "الفترة من {0} إلى {1}",
            ["asOf"] = "الوضع في {0}",
            ["notRecorded"] = "الكميات السابقة لأول إذن تسليم ({0}) غير مسجّلة: عقود ما قبل النظام أُدخلت كرصيد مالي فقط.",
            ["noIssue"] = "لا يوجد أي إذن تسليم مسجّل لهذا العميل.",
            ["provisionalNote"] = "البضاعة التي ما تزال لدى العميل مُقيّمة حتى تاريخ النهاية بأسعار العقد؛ يتغيّر هذا المبلغ عند إرجاعها.",
            ["warn.negativeBalance"] = "نزلت الكمية لدى العميل تحت الصفر في {0} على العقد {1} — راجع التواريخ المدخلة على الأذونات.",
            ["warn.overReturned"] = "إرجاع على العقد {1} يذكر {2} أكثر مما سُلّم.",
            ["warn.pricingCorrectionPending"] = "العقد {1} ما يزال قيد تحويل الأسعار الشهرية إلى يومية؛ قد تتغير قيمته.",
            ["col.sku"] = "المرجع",
            ["col.item"] = "الصنف",
            ["col.opening"] = "رصيد أول المدة",
            ["col.issued"] = "المسلّم",
            ["col.returned"] = "المرتجع",
            ["col.missingDamaged"] = "مفقود / تالف",
            ["col.transfers"] = "تحويل بين العقود",
            ["col.transferIn"] = "تحويل وارد",
            ["col.transferOut"] = "تحويل صادر",
            ["col.remaining"] = "لدى العميل",
            ["col.rate"] = "سعر الوحدة / اليوم",
            ["col.value"] = "قيمة الإيجار للفترة",
            ["col.date"] = "التاريخ",
            ["col.document"] = "المستند",
            ["col.contract"] = "العقد",
            ["col.movement"] = "الحركة",
            ["col.qty"] = "الكمية",
            ["col.balance"] = "الرصيد",
            ["col.branch"] = "الفرع",
            ["col.site"] = "الورشة",
            ["col.issueDate"] = "تاريخ التسليم",
            ["col.status"] = "الحالة",
            ["provisional"] = "منها تقديري",
            ["multipleRates"] = "أسعار متعددة",
            ["noRent"] = "بدون إيجار",
            ["total"] = "المجموع",
            ["status.open"] = "ساري",
            ["status.goodsBack"] = "أُرجعت البضاعة",
            ["status.closed"] = "مغلق",
            ["move.opening"] = "رصيد أول المدة",
            ["move.issue"] = "إذن تسليم",
            ["move.return"] = "إرجاع",
            ["move.partialReturn"] = "إرجاع جزئي",
            ["move.transferIn"] = "تحويل وارد",
            ["move.transferOut"] = "تحويل صادر",
        };

        public static readonly IReadOnlyDictionary<string, string> En = new Dictionary<string, string>
        {
            ["title"] = "Customer quantity statement",
            ["sheet.items"] = "By item",
            ["sheet.movements"] = "Movements",
            ["sheet.contracts"] = "By contract",
            ["period"] = "Period {0} to {1}",
            ["asOf"] = "As of {0}",
            ["notRecorded"] = "Quantities before the first issue note ({0}) are not recorded: contracts from before the system were opened as a money balance only.",
            ["noIssue"] = "No issue note recorded for this customer.",
            ["provisionalNote"] = "Goods still out are valued through the end date at the contract’s rates; this figure moves when they come back.",
            ["warn.negativeBalance"] = "The quantity held went below zero on {0} under contract {1} — check the dates typed on the notes.",
            ["warn.overReturned"] = "A return under contract {1} claims {2} more than was issued.",
            ["warn.pricingCorrectionPending"] = "Contract {1} is still being converted from monthly to daily prices; its value may change.",
            ["col.sku"] = "SKU",
            ["col.item"] = "Item",
            ["col.opening"] = "Opening",
            ["col.issued"] = "Issued",
            ["col.returned"] = "Returned",
            ["col.missingDamaged"] = "Missing / damaged",
            ["col.transfers"] = "Transfer between contracts",
            ["col.transferIn"] = "Transfer in",
            ["col.transferOut"] = "Transfer out",
            ["col.remaining"] = "With the customer",
            ["col.rate"] = "Unit price / day",
            ["col.value"] = "Rental value for the period",
            ["col.date"] = "Date",
            ["col.document"] = "Document",
            ["col.contract"] = "Contract",
            ["col.movement"] = "Movement",
            ["col.qty"] = "Quantity",
            ["col.balance"] = "Balance",
            ["col.branch"] = "Branch",
            ["col.site"] = "Site",
            ["col.issueDate"] = "Issue date",
            ["col.status"] = "Status",
            ["provisional"] = "of which provisional",
            ["multipleRates"] = "Multiple rates",
            ["noRent"] = "No rent",
            ["total"] = "Total",
            ["status.open"] = "Open",
            ["status.goodsBack"] = "Goods back",
            ["status.closed"] = "Closed",
            ["move.opening"] = "Opening",
            ["move.issue"] = "Issue note",
            ["move.return"] = "Return",
            ["move.partialReturn"] = "Partial return",
            ["move.transferIn"] = "Transfer in",
            ["move.transferOut"] = "Transfer out",
        };
    }
}
