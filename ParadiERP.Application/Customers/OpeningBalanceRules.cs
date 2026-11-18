namespace ParadiERP.Application.Customers;

/// <summary>
/// A customer's opening balance and its corrections (owner, 2026-10-10, go-live): what the
/// customer owed — or was owed — the day the company started on the system, entered once, and
/// put right afterwards only by a correction that says why.
///
/// <para>
/// Pure rules, so the server and its tests read one copy. The amount always travels positive with
/// a direction; the sign is decided here, in one place, because "a negative debt" typed into a
/// form is exactly how a credit balance gets entered as a debit.
/// </para>
/// </summary>
public static class OpeningBalanceRules
{
    /// <summary>The customer owes the company (a debit on their account).</summary>
    public const string OwesUs = "owesUs";

    /// <summary>The company owes the customer (a credit on their account).</summary>
    public const string WeOwe = "weOwe";

    /// <summary>A figure no customer of this business reaches; above it the field was mistyped.</summary>
    public const decimal MaxAmount = 1_000_000_000_000m;

    public const int MaxReasonLength = 400;

    /// <summary>+1 for <see cref="OwesUs"/>, −1 for <see cref="WeOwe"/>, null for anything else.</summary>
    public static int? Sign(string? direction) => direction switch
    {
        OwesUs => 1,
        WeOwe => -1,
        _ => null
    };

    /// <summary>
    /// What is wrong with an opening balance or a correction, or null. <paramref name="amount"/> is
    /// already rounded to the currency. A date after <paramref name="today"/> (the company's
    /// calendar) is refused: a balance "as of" next week is a typing error, never a fact.
    /// </summary>
    public static string? Problem(decimal amount, string? direction, DateOnly? on, DateOnly today)
    {
        if (Sign(direction) is null) return $"Say whether the customer owes the company ({OwesUs}) or the company owes the customer ({WeOwe}).";
        if (amount <= 0) return "An opening balance is an amount above zero; leave it empty when the customer owes nothing.";
        if (amount > MaxAmount) return "This amount is too large to be an opening balance.";
        if (on is DateOnly day && day > today) return "An opening balance cannot be dated in the future.";
        return null;
    }

    /// <summary>The reason a correction must carry, or the problem with it.</summary>
    public static string? ReasonProblem(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "Write the reason for the correction.";
        if (reason.Trim().Length > MaxReasonLength) return $"The reason is limited to {MaxReasonLength} characters.";
        return null;
    }

    /// <summary>
    /// Whether this caller may write the opening balance now. At registration whoever creates the
    /// customer may; afterwards only <c>customers.opening_balance</c>, and only while it was never
    /// set. Returns the refusal, or null.
    /// </summary>
    public static OpeningRefusal? MaySet(bool alreadySet, bool atRegistration, bool holdsPermission)
    {
        if (alreadySet) return OpeningRefusal.AlreadySet;
        if (!atRegistration && !holdsPermission) return OpeningRefusal.Forbidden;
        return null;
    }
}

public enum OpeningRefusal
{
    /// <summary>The opening balance was written before; a correction is the way to change it.</summary>
    AlreadySet,
    /// <summary>Setting it after registration needs <c>customers.opening_balance</c>.</summary>
    Forbidden
}
