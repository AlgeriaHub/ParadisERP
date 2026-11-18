using System.Globalization;
using ParadiERP.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Rentals;

/// <summary>A separate lifetime counter for each branch: CTR- + branch code + six-digit sequence.</summary>
public static class ContractNumbers
{
    public static async Task<string> NextAsync(PosDbContext db, Guid branchId, CancellationToken ct = default)
    {
        // Serialize the draw and insert, including callers outside the HTTP write filter.
        await db.LockVersionsAsync(ct);
        var code = await db.Branches.Where(b => b.Id == branchId).Select(b => b.Code).SingleAsync(ct);
        var prefix = "CTR-" + code;
        var numbers = await db.RentalContracts.AsNoTracking()
            .Where(c => c.Number.StartsWith(prefix)).Select(c => c.Number).ToListAsync(ct);
        var highest = numbers.Select(n => Sequence(n, prefix)).DefaultIfEmpty(0).Max();
        return prefix + checked(highest + 1).ToString("D6", CultureInfo.InvariantCulture);
    }

    private static long Sequence(string number, string prefix)
    {
        var tail = number.AsSpan(prefix.Length);
        return tail.Length >= 6 && long.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : 0;
    }
}
