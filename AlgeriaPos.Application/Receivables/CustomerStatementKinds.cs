namespace AlgeriaPos.Application.Receivables;

/// <summary>
/// What a line of a customer statement is (owner, 2026-10-10: one statement for everything — sales,
/// rentals, receipts, payments paid out, opening balance and adjustments — that the admin can narrow
/// to sales or to rentals). The screen labels and filters by it; the figures never depend on it.
/// </summary>
public static class CustomerStatementKinds
{
    public const string Sale = "sale";
    public const string Rental = "rental";
    public const string Receipt = "receipt";
    public const string Payment = "payment";
    public const string Opening = "opening";
    public const string Adjustment = "adjustment";
    /// <summary>A movement no rule below names yet. Shown under "all", in neither narrowed view.</summary>
    public const string Other = "other";
}

/// <summary>Which part of the account a statement shows.</summary>
public enum StatementFilter
{
    /// <summary>Every movement on account 411 for the customer, and every sale paid at the till.</summary>
    All = 0,
    /// <summary>Sales and their refunds, and the part of each receipt or payment that belongs to them.</summary>
    Sales = 1,
    /// <summary>Rentals, and the part of each receipt or payment that belongs to them.</summary>
    Rentals = 2,
}

public static class StatementFilters
{
    /// <summary>"all", "sales" or "rentals" (case ignored); null or empty is "all"; anything else is not a filter.</summary>
    public static StatementFilter? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" => StatementFilter.All,
        "sales" => StatementFilter.Sales,
        "rentals" => StatementFilter.Rentals,
        _ => null,
    };

    public static string Key(StatementFilter filter) => filter switch
    {
        StatementFilter.Sales => "sales",
        StatementFilter.Rentals => "rentals",
        _ => "all",
    };
}
