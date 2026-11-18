using AlgeriaPos.Application.Contracts;
using AlgeriaPos.Application.Customers;
using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;
using AlgeriaPos.Infrastructure.Catalog;
using AlgeriaPos.Infrastructure.Ledger;
using AlgeriaPos.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlgeriaPos.Infrastructure.Customers;

/// <summary>
/// A customer's opening balance and its corrections (owner, 2026-10-10: the company goes live
/// today, and customers who already owe money — or are owed it — start with that balance).
///
/// <para>
/// <b>Posted like the stock opening balance</b>: against opening-balance equity (890), never
/// revenue — this debt was run up before the system existed, and posting it as a sale would show
/// income that never happened in this period. The 411 side carries the customer, so the statement
/// (which <i>is</i> account 411 for one customer) starts from it.
/// </para>
/// <para>
/// Both methods run inside the caller's write scope and change the customer's running balance, so
/// the caller hands over the row read under the version lock (<see cref="PosDbContext.LockedAsync{T}"/>)
/// or a row it is adding in this same save. Neither saves.
/// </para>
/// </summary>
public sealed class CustomerOpeningBalances(PosDbContext db, LedgerPoster ledger, IRegionalSettingsProvider regional, TimeProvider time)
{
    /// <summary>The company's today, the bound an opening date may not pass.</summary>
    public DateOnly Today() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), StoreDocumentHistory.Zone(regional)).DateTime);

    /// <summary>
    /// What is wrong with this opening balance, or null. Rounds to the currency first, so "12.345"
    /// in a two-decimal currency is judged as the 12.35 that would be posted.
    /// </summary>
    public string? Problem(CustomerOpeningBalanceRequest r) =>
        OpeningBalanceRules.Problem(regional.Get().RoundMoney(r.Amount), r.Direction, r.On, Today());

    /// <summary>
    /// Writes the opening balance on <paramref name="customer"/> and posts it. The caller has
    /// checked <see cref="OpeningBalanceRules.MaySet"/> and <see cref="Problem"/>.
    /// </summary>
    public async Task ApplyAsync(Customer customer, CustomerOpeningBalanceRequest r, Guid branchId, string? by, CancellationToken ct)
    {
        var signed = OpeningBalanceRules.Sign(r.Direction)!.Value * regional.Get().RoundMoney(r.Amount);
        var on = r.On ?? Today();

        customer.OpeningBalance = signed;
        customer.OpeningBalanceOn = on;
        customer.OpeningBalanceSetAtUtc = time.GetUtcNow().UtcDateTime;
        customer.OpeningBalanceSetBy = by;
        customer.Balance += signed;

        var draft = new PostingDraft
        {
            EntryDate = EntryDate(on),
            BranchId = branchId,
            // The customer's own id: the poster writes one entry per source, so a second request
            // that slipped past the checks still cannot post the opening twice.
            SourceType = LedgerSources.CustomerOpening,
            SourceId = customer.Id,
            Description = $"Opening balance — {customer.Code} {customer.FullName}".Trim(),
            CreatedBy = by
        };
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.Customers, signed, memo: customer.Code, customerId: customer.Id));
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.OpeningBalance, -signed, memo: customer.Code));
        await ledger.PostAsync(draft, ct);
    }

    /// <summary>
    /// A correction of the opening balance with its reason: a numbered paper, an entry against 890
    /// for that customer, and the running balance moved by it. The caller has checked the
    /// permission, that the opening balance was set, and the request.
    /// </summary>
    public async Task<CustomerOpeningCorrection> CorrectAsync(
        Customer customer, OpeningCorrectionRequest r, Guid branchId, string? by, CancellationToken ct)
    {
        var signed = OpeningBalanceRules.Sign(r.Direction)!.Value * regional.Get().RoundMoney(r.Amount);
        var on = r.On ?? Today();
        var correction = new CustomerOpeningCorrection
        {
            CustomerId = customer.Id,
            BranchId = branchId,
            Amount = signed,
            EffectiveOn = on,
            Reason = r.Reason.Trim(),
            CreatedBy = by,
            CreatedAtUtc = time.GetUtcNow().UtcDateTime,
            // Under the version lock the caller holds, so two corrections draw one after another.
            Number = $"OBC-{await db.CustomerOpeningCorrections.CountAsync(ct) + 1:D6}"
        };
        db.CustomerOpeningCorrections.Add(correction);
        customer.Balance += signed;

        var draft = new PostingDraft
        {
            EntryDate = EntryDate(on),
            BranchId = branchId,
            SourceType = LedgerSources.CustomerOpeningCorrection,
            SourceId = correction.Id,
            Description = $"Opening balance correction {correction.Number} — {correction.Reason}",
            CreatedBy = by
        };
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.Customers, signed, memo: correction.Number, customerId: customer.Id));
        draft.AddIfAny(PostingLine.Signed(LedgerAccounts.OpeningBalance, -signed, memo: correction.Number));
        await ledger.PostAsync(draft, ct);
        return correction;
    }

    public static OpeningCorrectionDto ToDto(CustomerOpeningCorrection c) => new(
        c.Id, c.Number, c.CustomerId, c.Amount, c.EffectiveOn, c.Reason, c.CreatedBy, c.CreatedAtUtc);

    /// <summary>
    /// The instant a dated balance is filed at: the start of that day in the company's zone. A
    /// statement from that day onwards then opens with it, and one ending the day before does not.
    /// </summary>
    private DateTime EntryDate(DateOnly on)
    {
        var zone = StoreDocumentHistory.Zone(regional);
        var local = DateTime.SpecifyKind(on.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
