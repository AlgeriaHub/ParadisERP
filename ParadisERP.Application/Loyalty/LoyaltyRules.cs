namespace ParadisERP.Application.Loyalty;

/// <summary>A company's loyalty programme, as set in Back office → Store.</summary>
public sealed record LoyaltyProgram(bool Enabled, decimal PointsPerUnit, decimal PointValue, int MinRedeemPoints);

/// <summary>
/// Point arithmetic shared by the sale push and the tests. The web till mirrors it in
/// core/loyalty.ts.
/// </summary>
public static class LoyaltyRules
{
    /// <summary>
    /// Points are earned on what the customer really paid for: the sale total minus the parts
    /// settled with loyalty points (already a reward) or gift cards (points were due when the
    /// card was bought, not when it is spent). Always whole points, rounded down.
    /// </summary>
    public static decimal EligibleAmount(decimal grandTotal, decimal loyaltyPaid, decimal giftCardPaid) =>
        Math.Max(0, grandTotal - loyaltyPaid - giftCardPaid);

    public static int EarnedPoints(LoyaltyProgram program, decimal eligibleAmount) =>
        !program.Enabled || program.PointsPerUnit <= 0 || eligibleAmount <= 0
            ? 0
            : (int)Math.Floor(eligibleAmount * program.PointsPerUnit);

    /// <summary>Points consumed to pay an amount; rounded up so the shop never gives value away.</summary>
    public static int PointsForAmount(LoyaltyProgram program, decimal amount) =>
        program.PointValue <= 0 || amount <= 0 ? 0 : (int)Math.Ceiling(amount / program.PointValue);

    /// <summary>Largest amount a balance can pay, or 0 below the redemption threshold.</summary>
    public static decimal MaxRedeemValue(LoyaltyProgram program, int points) =>
        !program.Enabled || program.PointValue <= 0 || points < Math.Max(1, program.MinRedeemPoints)
            ? 0
            : points * program.PointValue;

    public static void Validate(LoyaltyProgram program)
    {
        if (program.PointsPerUnit < 0 || program.PointsPerUnit > 1000)
            throw new ArgumentException("Points per currency unit must be between 0 and 1000.");
        if (program.PointValue < 0 || program.PointValue > 1_000_000)
            throw new ArgumentException("The value of a point must be between 0 and 1,000,000.");
        if (program.MinRedeemPoints < 0) throw new ArgumentException("The redemption threshold cannot be negative.");
        if (program.Enabled && (program.PointsPerUnit <= 0 || program.PointValue <= 0))
            throw new ArgumentException("An enabled programme needs points per unit and a point value above zero.");
    }
}

public static class GiftCardRules
{
    /// <summary>Why a card cannot pay an amount right now, or null when it can.</summary>
    public static string? Problem(bool isActive, decimal balance, DateTime? expiresAtUtc, decimal amount, DateTime nowUtc)
    {
        if (!isActive) return "inactive";
        if (expiresAtUtc is DateTime expires && expires <= nowUtc) return "expired";
        if (amount > balance) return "insufficient";
        return null;
    }

    public static string NormalizeCode(string? code) => (code ?? "").Trim().ToUpperInvariant();

    /// <summary>
    /// SHA-256 of the normalised code, lower-case hex: what a till holds to find a card without
    /// holding the code (security review, 2026-10-10). The till computes the same over what the
    /// customer hands over (core/sha256.ts), so the two must normalise identically.
    /// </summary>
    public static string CodeHash(string? code) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(NormalizeCode(code)))).ToLowerInvariant();

    /// <summary>The last four characters for the receipt ("…7KQ2"); a short code shows only its last two.</summary>
    public static string CodeHint(string? code)
    {
        var normalized = NormalizeCode(code);
        var keep = normalized.Length > 8 ? 4 : Math.Min(2, normalized.Length);
        return "…" + normalized[^keep..];
    }
}
