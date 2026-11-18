using ParadisERP.Application.Contracts;
using ParadisERP.Application.Customers;
using ParadisERP.Domain;
using ParadisERP.Infrastructure.Persistence;
using ParadisERP.Infrastructure.Rentals;
using Microsoft.EntityFrameworkCore;

namespace ParadisERP.Infrastructure.Customers;

/// <summary>
/// The company customer's authorised representative (المخوَّل), as a dated history with one
/// current entry.
///
/// <para>
/// A service rather than endpoint lambdas because the one rule worth testing is here: a change
/// of representative is <b>one write</b> that ends every open row and adds the new one, so the
/// history keeps who dealt with the company and when, and there is never a moment with nobody —
/// or two people — in charge. Nothing here edits or deletes a row that has been written; the
/// only value ever set afterwards is <see cref="CustomerRepresentative.ToUtc"/>, once.
/// </para>
/// <para>
/// Results are <see cref="RentalResult{T}"/>-shaped — a value, or a failure kind and its one
/// English sentence — so the catalog endpoints map them the way the rental endpoints do.
/// </para>
/// </summary>
public sealed class CustomerRepresentatives(PosDbContext db)
{
    // ---------- reading ----------

    /// <summary>"Current" the way every reader takes it: no end date, latest start first.</summary>
    public Task<CustomerRepresentative?> CurrentAsync(Guid customerId, CancellationToken ct = default) =>
        db.CustomerRepresentatives.AsNoTracking()
            .Where(x => x.CustomerId == customerId && x.ToUtc == null)
            .OrderByDescending(x => x.FromUtc)
            .FirstOrDefaultAsync(ct);

    /// <summary>The current representative of each of these customers, in one query, for the list.</summary>
    public async Task<Dictionary<Guid, CustomerRepresentative>> CurrentByCustomerAsync(
        IReadOnlyCollection<Guid> customerIds, CancellationToken ct = default)
    {
        if (customerIds.Count == 0) return new();
        var open = await db.CustomerRepresentatives.AsNoTracking()
            .Where(x => x.ToUtc == null && customerIds.Contains(x.CustomerId))
            .ToListAsync(ct);
        return open.GroupBy(x => x.CustomerId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.FromUtc).First());
    }

    /// <summary>
    /// The whole history, newest first — for any customer that exists, deleted or not. History
    /// is history: the receivables account of a tombstoned company may still need who signed.
    /// </summary>
    public async Task<RentalResult<List<RepresentativeDto>>> HistoryAsync(Guid customerId, CancellationToken ct = default)
    {
        if (!await db.Customers.AnyAsync(c => c.Id == customerId, ct))
            return RentalResult<List<RepresentativeDto>>.NotFound("Customer not found.");
        return RentalResult<List<RepresentativeDto>>.Success(await ListAsync(customerId, ct));
    }

    private async Task<List<RepresentativeDto>> ListAsync(Guid customerId, CancellationToken ct)
    {
        var rows = await db.CustomerRepresentatives.AsNoTracking()
            .Where(x => x.CustomerId == customerId)
            .OrderByDescending(x => x.FromUtc).ThenByDescending(x => x.CreatedAtUtc)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    // ---------- writing ----------

    /// <summary>
    /// The first row, written inside the caller's write scope so the customer and the person
    /// who signs for it are one save. The caller has already run <c>CustomerRules.KindProblem</c>;
    /// this only writes.
    /// </summary>
    public CustomerRepresentative Appoint(Customer customer, RepresentativeRequest r, string? by)
    {
        var now = DateTime.UtcNow;
        var row = new CustomerRepresentative
        {
            CustomerId = customer.Id,
            FullName = r.FullName.Trim(),
            IdDocumentNumber = TrimOrNull(r.IdDocumentNumber),
            IdDocumentType = IdDocumentTypes.Normalize(r.IdDocumentType),
            IdDocumentIssuer = TrimOrNull(r.IdDocumentIssuer),
            IdDocumentIssuedOn = r.IdDocumentIssuedOn,
            Phone = TrimOrNull(r.Phone),
            Note = TrimOrNull(r.Note),
            FromUtc = now,
            ToUtc = null,
            CreatedBy = by,
            CreatedAtUtc = now
        };
        db.CustomerRepresentatives.Add(row);
        return row;
    }

    /// <summary>
    /// A company turned into a person has nobody signing for it any more: every open row is
    /// ended, inside the caller's scope. The history is kept and simply not shown while the kind
    /// is Person; switching back later appoints again — an appointment, never a resurrection.
    /// </summary>
    public async Task EndCurrentAsync(Guid customerId, DateTime atUtc, CancellationToken ct = default)
    {
        var open = await db.CustomerRepresentatives
            .Where(x => x.CustomerId == customerId && x.ToUtc == null).ToListAsync(ct);
        foreach (var row in open) row.ToUtc = atUtc;
    }

    /// <summary>
    /// The representative leaves, or the first one is appointed after the fact: end every open
    /// row at the takeover date and add the new one, in one unit. The customer row is not
    /// touched — nothing replicated changed, and a version bump would re-pull it on every till
    /// for nothing.
    /// </summary>
    public async Task<RentalResult<List<RepresentativeDto>>> ChangeAsync(
        Guid customerId, ChangeRepresentativeRequest r, string? by, CancellationToken ct = default)
    {
        var customer = await db.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == customerId && !c.IsDeleted, ct);
        if (customer is null) return RentalResult<List<RepresentativeDto>>.NotFound("Customer not found.");
        if (customer.Kind != CustomerKind.Company)
            return RentalResult<List<RepresentativeDto>>.Conflict("Only a company has an authorised representative.");

        var now = DateTime.UtcNow;
        var from = r.FromUtc ?? now;
        var current = await CurrentAsync(customerId, ct);

        if (CustomerRules.RepresentativeProblem(r.FullName, from, current?.FromUtc, now) is string problem)
            return RentalResult<List<RepresentativeDto>>.Invalid(problem switch
            {
                "nameRequired" => "The representative's full name is required.",
                "startsBeforeCurrent" => "The new representative cannot start before the current one did.",
                "startsInFuture" => "The takeover date cannot be in the future.",
                _ => problem
            });
        if (IdDocumentTypes.IdentityProblem(r.IdDocumentType, r.IdDocumentNumber, r.IdDocumentIssuer,
                r.IdDocumentIssuedOn, null, IdDocumentTypes.TodayAnywhere(now)) is string idProblem)
            return RentalResult<List<RepresentativeDto>>.Invalid(idProblem);

        // Never BeginWriteAsync: the catalog group already runs inside the subscription
        // filter's transaction, and the scope joins it rather than failing on a nested one.
        await using var tx = await db.BeginWriteScopeAsync(ct);

        // Every open row, not the one loaded above: ending them all is what keeps a stray
        // duplicate — two managers at the same second, a hand-run SQL fix — from surviving.
        await EndCurrentAsync(customerId, from, ct);
        db.CustomerRepresentatives.Add(new CustomerRepresentative
        {
            CustomerId = customerId,
            FullName = r.FullName.Trim(),
            IdDocumentNumber = TrimOrNull(r.IdDocumentNumber),
            IdDocumentType = IdDocumentTypes.Normalize(r.IdDocumentType),
            IdDocumentIssuer = TrimOrNull(r.IdDocumentIssuer),
            IdDocumentIssuedOn = r.IdDocumentIssuedOn,
            Phone = TrimOrNull(r.Phone),
            Note = TrimOrNull(r.Note),
            FromUtc = from,
            ToUtc = null,
            CreatedBy = by,
            CreatedAtUtc = now
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return RentalResult<List<RepresentativeDto>>.Success(await ListAsync(customerId, ct));
    }

    // ---------- mapping ----------

    public static RepresentativeDto ToDto(CustomerRepresentative x) => new(
        x.Id, x.CustomerId, x.FullName, x.IdDocumentNumber, x.Phone, x.Note,
        x.FromUtc, x.ToUtc, x.CreatedBy, x.CreatedAtUtc,
        x.IdDocumentType, x.IdDocumentIssuer, x.IdDocumentIssuedOn);

    public static AdminCustomerDto ToDto(Customer c, CustomerRepresentative? current) => new(
        c.Id, c.Code, c.FullName, c.Phone, c.Address, c.TaxId, c.Email, c.BirthDate,
        c.CreditLimit, c.Balance, c.LoyaltyPoints, c.Version,
        c.Kind.ToString(), c.IdDocumentNumber,
        // A person has no representative whatever the table holds (a company turned person has
        // its rows ended anyway); only a company's current one is shown.
        c.Kind == CustomerKind.Company && current is not null ? ToDto(current) : null,
        c.IdDocumentType, c.IdDocumentIssuer, c.IdDocumentIssuedOn,
        c.FirstName, c.FamilyName,
        c.FiscalIds,
        c.OpeningBalance, c.OpeningBalanceOn, c.OpeningBalanceSetAtUtc, c.OpeningBalanceSetBy);

    private static string? TrimOrNull(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
