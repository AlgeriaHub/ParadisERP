using ParadiERP.Domain;

namespace ParadiERP.Application.Catalog;

/// <summary>
/// How the catalogue is handed out a page at a time.
///
/// <para>
/// The back office keeps one in-memory copy of the catalogue and searches it in the browser,
/// so it needs the whole list — and a single response carrying the whole list is the one a
/// slow line never finishes. It therefore walks pages of <see cref="MaxTake"/>, and the only
/// thing that can go wrong with that is the order changing between two requests: a product
/// that moves across a page boundary is listed twice, and the one it displaced never. Hence
/// the tie-break on the id — two products with the same French name have no natural order,
/// and without it the database is free to give them a different one each time.
/// </para>
/// <para>
/// A caller that names no page gets the old behaviour: the first <see cref="DefaultTake"/>
/// rows, no skip. The e2e scripts and the operator portal still call it that way.
/// </para>
/// </summary>
public static class ProductPaging
{
    /// <summary>What a caller that does not page gets: the cap that stood before paging.</summary>
    public const int DefaultTake = 5000;

    /// <summary>The most one page may carry. A thousand rows is a few hundred kilobytes.</summary>
    public const int MaxTake = 1000;

    /// <summary>
    /// The query ordered and cut to one page. Pure LINQ, so it translates to SQL on every
    /// provider and runs in memory under test.
    /// </summary>
    public static IQueryable<Product> Page(IQueryable<Product> products, int? skip, int? take)
    {
        // NameAr before the id: articles loaded from an Arabic stock sheet have no French name yet,
        // and ordered by id alone they would page in no order a person could follow.
        var ordered = products.OrderBy(p => p.NameFr).ThenBy(p => p.NameAr).ThenBy(p => p.Id);
        if (take is null) return ordered.Take(DefaultTake);

        // A request for nothing, or for a negative page, is a client bug; answering with one
        // row rather than an error keeps the walk terminating and makes the bug visible.
        var size = Math.Clamp(take.Value, 1, MaxTake);
        var offset = Math.Max(0, skip ?? 0);
        return ordered.Skip(offset).Take(size);
    }
}
