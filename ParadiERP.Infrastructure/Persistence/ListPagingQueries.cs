using ParadiERP.Application.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ParadiERP.Infrastructure.Persistence;

/// <summary>
/// A page of an ordered query, counted and cut in the database (<see cref="ListPaging"/>). The
/// query must already be ordered with a tie-break on a unique column (usually the id): without it
/// two requests may order equal rows differently and a row shows on two pages and another on none.
/// </summary>
public static class ListPagingQueries
{
    public static async Task<PageResult<T>> ToPageAsync<T>(
        this IQueryable<T> ordered, int? skip, int? take, CancellationToken ct, object? summary = null)
    {
        var size = ListPaging.Take(take);
        var total = await ordered.CountAsync(ct);
        var offset = ListPaging.Fit(ListPaging.Skip(skip), size, total);
        var items = total == 0 ? [] : await ordered.Skip(offset).Take(size).ToListAsync(ct);
        return new PageResult<T>(items, total, offset, size, summary);
    }

    /// <summary>The same, mapping the page's rows to what the screen reads (a DTO that needs other rows, say).</summary>
    public static async Task<PageResult<TOut>> ToPageAsync<T, TOut>(
        this IQueryable<T> ordered, int? skip, int? take, Func<List<T>, Task<IReadOnlyList<TOut>>> map,
        CancellationToken ct, object? summary = null)
    {
        var page = await ordered.ToPageAsync(skip, take, ct, summary);
        return new PageResult<TOut>(await map(page.Items.ToList()), page.Total, page.Skip, page.Take, summary);
    }
}
