using ParadisERP.Application.Regional;

namespace ParadisERP.Application.Payroll;

/// <summary>
/// A progressive scale, applied.
///
/// <para>
/// The brackets themselves are never in this file. They are settings the accountant enters and
/// the validator keeps ordered and open at the top; all that lives here is the walk over them,
/// which is the same arithmetic in every jurisdiction that taxes by slices.
/// </para>
/// </summary>
public static class IncomeTax
{
    /// <summary>
    /// Taxes each slice of <paramref name="taxBase"/> at the rate of the bracket it falls in, and
    /// the remainder at the open-top bracket's rate.
    ///
    /// <para>
    /// Returns an <b>unrounded</b> figure: the caller rounds once, through the regional helper,
    /// so the currency's own number of decimals decides and rounding never happens twice.
    /// </para>
    /// <para>
    /// A base at or below zero is taxed nothing. That is arithmetic, not a policy: there is no
    /// slice to tax. It is not the same as an unconfigured scale, which
    /// <see cref="PayrollEngine.Refusal"/> refuses before anything reaches here.
    /// </para>
    /// </summary>
    public static decimal Progressive(decimal taxBase, IReadOnlyList<TaxBracket>? brackets)
    {
        if (taxBase <= 0m || brackets is null || brackets.Count == 0) return 0m;

        decimal tax = 0m;
        decimal below = 0m;   // top of the bracket already taxed

        foreach (var bracket in brackets)
        {
            if (taxBase <= below) break;

            // The open-top bracket takes everything left; the validator guarantees there is
            // exactly one and that it is last.
            var top = bracket.UpTo ?? taxBase;
            var slice = Math.Min(taxBase, top) - below;
            if (slice > 0m) tax += slice * bracket.Rate / 100m;

            below = top;
        }

        return tax;
    }

    /// <summary>The same walk, rounded once at the end to the currency's decimals.</summary>
    public static decimal Progressive(RegionalSettings regional, decimal taxBase, IReadOnlyList<TaxBracket>? brackets)
    {
        ArgumentNullException.ThrowIfNull(regional);
        return regional.RoundMoney(Progressive(taxBase, brackets));
    }
}
