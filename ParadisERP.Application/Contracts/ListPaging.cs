namespace ParadisERP.Application.Contracts;

/// <summary>
/// One page of a list the server pages (owner, 2026-11-17: "I need to paginate in backend, not
/// frontend"). <paramref name="Total"/> counts every row the filters keep, not just this page;
/// <paramref name="Skip"/> is the offset actually used — a page past the end, after a filter shrank
/// the list, is answered with the last page. <paramref name="Summary"/> carries a list's figures
/// over all its filtered rows (the tiles above it, a totals row), never over the page alone.
/// </summary>
public sealed record PageResult<T>(IReadOnlyList<T> Items, int Total, int Skip, int Take, object? Summary = null);

/// <summary>The sizes a list offers (10, 20, 50 or 100; ten to begin with) and the clamps every page route applies.</summary>
public static class ListPaging
{
    public static readonly IReadOnlyList<int> Sizes = [10, 20, 50, 100];
    public const int DefaultTake = 10;
    public const int MaxTake = 100;

    public static int Take(int? take) => Math.Clamp(take ?? DefaultTake, 1, MaxTake);

    public static int Skip(int? skip) => Math.Max(0, skip ?? 0);

    /// <summary>The offset to answer with: a page past the end becomes the last page.</summary>
    public static int Fit(int skip, int take, int total) =>
        total == 0 ? 0 : skip < total ? skip : (total - 1) / take * take;

    /// <summary>
    /// A page of a list the server computes in memory (a report, a reconciliation): the whole list
    /// is built as before and only the page is sent.
    /// </summary>
    public static PageResult<T> Of<T>(IReadOnlyList<T> all, int? skip, int? take, object? summary = null)
    {
        var size = Take(take);
        var offset = Fit(Skip(skip), size, all.Count);
        return new PageResult<T>(all.Skip(offset).Take(size).ToList(), all.Count, offset, size, summary);
    }
}
