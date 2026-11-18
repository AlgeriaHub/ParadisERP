using AlgeriaPos.Application.Contracts;

namespace AlgeriaPos.Application.Catalog;

/// <summary>
/// Wording to start from, so the printed documents can be exercised before the owner's own
/// paragraphs exist.
///
/// <para>
/// Every one of these begins with the line <see cref="Marker"/> in its own language, and the
/// line is part of the text: it is stored with it and it prints with it. That is deliberate.
/// The rule this module was built under is that the code writes no legal terms — a contract
/// clause invented here and printed under a company's name is a liability, not a convenience.
/// What this class offers instead is something obviously provisional that a person can read,
/// edit and replace, and that says what it is on the paper if anyone prints it before they do.
/// </para>
/// <para>
/// Nothing here is seeded. The admin asks for it from the store screen, sees it in the form, and
/// saves it himself — at which point it is his text, stored on the Store row like any other, and
/// editable from the same screen. That also means the sample costs one of the year's changes
/// only if he decides to keep it.
/// </para>
/// </summary>
public static class SampleDocumentTexts
{
    public static class Marker
    {
        public const string Ar = "—— نموذج تجريبي: استبدله بنص شركتك قبل الاستعمال ——";
        public const string Fr = "—— Exemple : remplacez-le par le texte de votre société avant usage ——";
        public const string En = "—— Sample: replace with your company's wording before use ——";
    }

    /// <summary>Contact lines to start from. The bank details are as obviously invented as the terms.</summary>
    public const string SampleEmail = "contact@example.dz";
    public const string SampleWebsite = "https://www.example.dz";
    public const string SampleBankDetails =
        "—— Exemple —— Banque : BANQUE EXEMPLE, agence 00000 · RIB 000 00000 0000000000 00 · IBAN DZ00 0000 0000 0000 0000 0000";

    /// <summary>
    /// The sample wording, ready for <c>StoreDocumentsDto.DocumentTexts</c>: kind, then language.
    /// Built fresh on each call — the caller may edit what it gets.
    /// </summary>
    public static Dictionary<string, Dictionary<string, string>> All() => new()
    {
        [DocumentTextKinds.Invoice] = Text(
            ar: """
                الدفع خلال ثلاثين (30) يوماً من تاريخ الفاتورة.
                يترتب على التأخير في السداد فائدة تأخير وفق الشروط المتفق عليها.
                تبقى البضاعة ملكاً للبائع حتى سداد ثمنها كاملاً.
                """,
            fr: """
                Paiement à trente (30) jours date de facture.
                Tout retard de paiement entraîne des intérêts de retard aux conditions convenues.
                Les marchandises restent la propriété du vendeur jusqu'au paiement intégral.
                """,
            en: """
                Payment within thirty (30) days of the invoice date.
                Late payment carries interest at the agreed rate.
                Goods remain the property of the seller until paid for in full.
                """),

        [DocumentTextKinds.CreditNote] = Text(
            ar: "يُخصم هذا الإشعار من رصيد الزبون، ولا يُصرف نقداً.",
            fr: "Cet avoir s'impute sur le compte du client et n'est pas remboursé en espèces.",
            en: "This credit note is applied to the customer's account and is not refunded in cash."),

        [DocumentTextKinds.ReceiptVoucher] = Text(
            ar: """
                يُعدّ هذا السند إقراراً بالاستلام فقط.
                قبض الشيك أو الحوالة لا يبرئ الذمة إلا بعد التحصيل الفعلي.
                """,
            fr: """
                Ce reçu vaut accusé de réception.
                La remise d'un chèque ou d'un virement ne libère le débiteur qu'après encaissement effectif.
                """,
            en: """
                This voucher is an acknowledgement of receipt.
                A cheque or transfer discharges the debt only once it has actually cleared.
                """),

        [DocumentTextKinds.Statement] = Text(
            ar: "يُرجى مراجعة هذا الكشف وإبلاغنا بأي اختلاف خلال خمسة عشر (15) يوماً من تاريخه.",
            fr: "Merci de vérifier ce relevé et de nous signaler toute différence dans les quinze (15) jours.",
            en: "Please check this statement and report any difference within fifteen (15) days."),

        [DocumentTextKinds.RentalContract] = Text(
            ar: """
                1. يقرّ المستأجر باستلام المعدات المبيّنة أعلاه بحالة سليمة وصالحة للاستعمال.
                2. تبدأ مدة الإيجار من تاريخ إذن الإخراج، وتُحتسب بالأيام، وأقلّها يوم واحد.
                3. المستأجر مسؤول عن حفظ المعدات واستعمالها فيما خُصّصت له، ولا يجوز له تأجيرها للغير.
                4. في حال تجاوز المدة المتفق عليها يُحتسب سعر التأخير المبيّن أمام كل بند.
                5. في حال التلف الكلي أو عدم الإرجاع يلتزم المستأجر بسداد قيمة التلف المبيّنة أمام البند.
                6. في حال التلف الجزئي تُحدَّد قيمة الإصلاح عند الاستلام ويلتزم المستأجر بسدادها.
                7. يبقى المؤجّر مالكاً للمعدات طوال مدة العقد.
                """,
            fr: """
                1. Le locataire reconnaît avoir reçu le matériel désigné ci-dessus en bon état de marche.
                2. La location court à compter du bon de sortie, se compte en jours, et au minimum un jour.
                3. Le locataire répond de la garde du matériel et de son usage conforme ; la sous-location est interdite.
                4. Au-delà de la durée convenue, le tarif de retard indiqué en regard de chaque ligne s'applique.
                5. En cas de perte ou de destruction, le locataire règle la valeur de remplacement indiquée en regard de la ligne.
                6. En cas de détérioration partielle, le coût de remise en état est arrêté à la restitution et reste à sa charge.
                7. Le matériel demeure la propriété du loueur pendant toute la durée du contrat.
                """,
            en: """
                1. The hirer acknowledges receiving the equipment above in good working order.
                2. Hire runs from the issue note, is counted in days, and is never less than one day.
                3. The hirer is responsible for the equipment's safekeeping and proper use; sub-hiring is not permitted.
                4. Beyond the agreed period the overdue rate shown against each line applies.
                5. If a unit is lost or destroyed the hirer pays the replacement value shown against its line.
                6. If a unit is damaged, the cost of repair is settled on return and is the hirer's.
                7. The equipment remains the owner's property for the whole term of the contract.
                """),

        [DocumentTextKinds.IssueNote] = Text(
            ar: "استلمتُ المعدات المبيّنة أعلاه بالكميات والحالة المذكورة.",
            fr: "J'ai reçu le matériel désigné ci-dessus dans les quantités et l'état indiqués.",
            en: "I have received the equipment above in the quantities and condition stated."),

        [DocumentTextKinds.ReturnNote] = Text(
            ar: "تمّ إرجاع المعدات بالحالة المبيّنة أعلاه، وتُحتسب قيمة أي تلف عند التسوية.",
            fr: "Le matériel a été restitué dans l'état indiqué ci-dessus ; toute détérioration est arrêtée au décompte.",
            en: "The equipment was returned in the condition stated above; any damage is settled at the reckoning."),

        [DocumentTextKinds.Payslip] = Text(
            ar: "يُرجى الاحتفاظ بهذه القسيمة. لأي استفسار يُراجع مسؤول شؤون الموظفين.",
            fr: "Conservez ce bulletin. Pour toute question, adressez-vous au service du personnel.",
            en: "Please keep this payslip. Any question goes to the personnel office."),

        [DocumentTextKinds.PaymentVoucher] = Text(
            ar: "استلمتُ المبلغ المبيّن أعلاه، ويُعدّ هذا السند إبراءً للذمة في حدوده.",
            fr: "J'ai reçu la somme indiquée ci-dessus ; le présent bon vaut quittance à due concurrence.",
            en: "I have received the amount stated above; this voucher discharges it to that extent."),
    };

    private static Dictionary<string, string> Text(string ar, string fr, string en) => new()
    {
        ["ar"] = Marker.Ar + "\n" + ar.Trim(),
        ["fr"] = Marker.Fr + "\n" + fr.Trim(),
        ["en"] = Marker.En + "\n" + en.Trim(),
    };

    /// <summary>Whether a text is still one of these — what the screen and the print frame warn on.</summary>
    public static bool IsSample(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && (text.Contains(Marker.Ar, StringComparison.Ordinal)
            || text.Contains(Marker.Fr, StringComparison.Ordinal)
            || text.Contains(Marker.En, StringComparison.Ordinal));

    /// <summary>The whole sample, in the shape the documents PUT accepts.</summary>
    public static StoreDocumentsDto Dto() =>
        new(All().ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, string>)p.Value),
            SampleEmail, SampleWebsite, SampleBankDetails);
}
