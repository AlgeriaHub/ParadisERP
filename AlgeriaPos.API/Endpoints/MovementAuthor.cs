using System.Security.Claims;
using AlgeriaPos.Domain;

namespace AlgeriaPos.API.Endpoints;

/// <summary>
/// Who a stock movement is recorded against (owner, 2026-10-12: "stock movements store the user"):
/// the token's <c>sub</c> and <c>name</c>, read the way the treasury endpoints read them, with the
/// name kept as it stood so a renamed or removed account still reads in the history.
/// </summary>
public readonly record struct MovementAuthor(Guid? UserId, string? UserName)
{
    public static MovementAuthor Of(ClaimsPrincipal user) => new(
        Guid.TryParse(user.FindFirstValue("sub"), out var id) ? id : null,
        Cut(user.FindFirstValue("name") ?? user.Identity?.Name, 200));

    /// <summary>A document number that fits the column, or null when there is none.</summary>
    public static string? DocumentNumber(string? number) => Cut(number, StockMovement.DocumentNumberMaxLength);

    private static string? Cut(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
