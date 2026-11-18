using AlgeriaPos.Domain;

namespace AlgeriaPos.Infrastructure.Manufacturing;

/// <summary>
/// Manufacturing labour privacy, the complete version (owner, 2026-11-03; costing-b2.md §8). Labour money for one
/// order — and every figure of one order that gives it back by subtraction: its total and unit cost, its released
/// value, its work in progress, its spoilage, its variance total, its frozen totals — goes only to a reader who can see
/// pay rates, that is who holds both <c>payroll.manage</c> and <c>manufacturing.costs.rates.view</c>. Everyone else
/// sees an order's material lines, and labour and totals only added up by production line, and a line's sum is
/// withheld too when it rests on one order or one worker. The frozen cost history (B2) is rate access only.
///
/// <para>
/// One way back is left, by the owner's choice: once production posts finished goods into stock at cost, the
/// product's cost price shows its unit cost to whoever sees stock.
/// </para>
/// </summary>
public static class CostPrivacy
{
    /// <summary>The refusal's code where a whole answer is withheld (the cost history).</summary>
    public const string WithheldCode = "payroll_details_withheld";

    public const string WithheldMessage =
        "The cost history shows labour by order, which gives pay rates back: it needs payroll.manage and manufacturing.costs.rates.view.";

    /// <summary>A line's sum is shown to a reader without rate access only from this many orders and this many workers.</summary>
    public const int MinimumOrders = 2;
    public const int MinimumWorkers = 2;

    /// <summary>The reader sees pay rates, and with them every per-order figure.</summary>
    public static bool RatesShown(MfgCaller caller) => RatesShown(caller.Has);

    /// <summary>The same rule for a caller read another way (maintenance passes the token's keys): one rule for both modules.</summary>
    public static bool RatesShown(Func<string, bool> has) =>
        has(PermissionCatalog.PayrollManage) && has(PermissionCatalog.ManufacturingCostsRatesView);

    /// <summary>Whether a line's sum may be shown: always with rate access, otherwise from two orders and two workers.</summary>
    public static bool LineSumShown(bool ratesShown, int orders, int workers) =>
        ratesShown || (orders >= MinimumOrders && workers >= MinimumWorkers);
}
