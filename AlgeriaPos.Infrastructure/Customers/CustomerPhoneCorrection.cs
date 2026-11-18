using AlgeriaPos.Application.Regional;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.Customers;

/// <summary>
/// Rewrites the phones saved before the mobile rule (owner, 2026-11-17) in the form a save gives
/// them now: 06.58.41.77.30 → +213658417730. Customers and their representatives. A phone it
/// cannot read as a mobile — a landline, two numbers in one box — is left exactly as typed, and the
/// customers list offers it under "Phone to fix" (owner's choice: keep, never empty).
///
/// <para>Idempotent: only phones not already in the stored form are read, so after the first
/// start it touches nothing but those left to fix by hand. A customer it rewrites goes through
/// <see cref="PosDbContext.SaveWithVersionsAsync"/>, so the tills pull the new form.</para>
/// </summary>
public sealed class CustomerPhoneCorrection(PosDbContext db, IRegionalSettingsProvider regional)
{
    public async Task<int> ApplyAsync(CancellationToken ct = default)
    {
        var rules = regional.Get().Phone;
        if (rules is null || !rules.IsSet) return 0;

        // What a stored-form phone can never be: no +, or a separator left in. Everything else
        // is already +digits and is not read again.
        var customers = await db.Customers
            .Where(c => c.Phone != null && (!c.Phone.StartsWith("+") || c.Phone.Contains(" ") || c.Phone.Contains(".")
                || c.Phone.Contains("-") || c.Phone.Contains("(") || c.Phone.Contains(")")))
            .ToListAsync(ct);
        var representatives = await db.CustomerRepresentatives
            .Where(r => r.Phone != null && (!r.Phone.StartsWith("+") || r.Phone.Contains(" ") || r.Phone.Contains(".")
                || r.Phone.Contains("-") || r.Phone.Contains("(") || r.Phone.Contains(")")))
            .ToListAsync(ct);

        var changedCustomers = new List<Domain.Customer>();
        foreach (var customer in customers)
        {
            if (Rewrite(customer.Phone, rules) is not string phone) continue;
            customer.Phone = phone;
            changedCustomers.Add(customer);
        }
        var changedRepresentatives = 0;
        foreach (var representative in representatives)
        {
            if (Rewrite(representative.Phone, rules) is not string phone) continue;
            representative.Phone = phone;
            changedRepresentatives++;
        }
        if (changedCustomers.Count == 0 && changedRepresentatives == 0)
        {
            db.ChangeTracker.Clear();
            return 0;
        }

        await using var tx = await db.BeginWriteScopeAsync(ct);
        await db.SaveWithVersionsAsync(ct, [.. changedCustomers]);
        await tx.CommitAsync(ct);
        return changedCustomers.Count + changedRepresentatives;
    }

    /// <summary>The stored form when the phone reads as a mobile and differs from it; otherwise null.</summary>
    private static string? Rewrite(string? stored, PhoneNumberRules rules)
    {
        var reading = PhoneNumbers.Normalize(stored, rules);
        return reading.IsMobile && reading.Value is not null && !string.Equals(reading.Value, stored, StringComparison.Ordinal)
            ? reading.Value
            : null;
    }
}
