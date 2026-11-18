namespace ParadiERP.Application.Regional;

/// <summary>
/// Tax on a document, charged the one way every document in this system charges it.
///
/// <para>
/// <b>On each rate's own total, not line by line.</b> A sale, a rental note and an invoice all
/// follow this rule; rounding each line first and adding the results up drifts by centimes from
/// the figure the customer checks and the business declares, and once two documents in the same
/// system round differently nobody can say which of the two is the invoice.
/// </para>
/// <para>
/// Grouping by rate rather than taking one rate for the document is what keeps it correct when
/// an invoice carries several — concrete at one rate and haulage at another on the same sheet is
/// ordinary here.
/// </para>
/// </summary>
public static class DocumentTax
{
    /// <summary>
    /// Tax on each rate's own net total, summed, and rounded through the company's own currency
    /// rather than to a constant number of places: three-decimal currencies exist and this
    /// codebase already serves one.
    /// </summary>
    /// <param name="netByRate">Net amount per tax rate, as a percentage — 19 means 19%.</param>
    public static decimal On(RegionalSettings regional, IReadOnlyDictionary<decimal, decimal> netByRate)
    {
        var tax = 0m;
        foreach (var (rate, net) in netByRate)
        {
            // A zero rate contributes nothing, which is exactly what a company that charges no
            // tax on a kind of document gets — the default, not a special case.
            if (rate <= 0 || net == 0) continue;
            tax += regional.RoundMoney(net * rate / 100m);
        }
        return tax;
    }
}
