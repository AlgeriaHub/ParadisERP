using AlgeriaPos.Application.Catalog;
using AlgeriaPos.Domain;

namespace AlgeriaPos.Tests.Catalog;

/// <summary>
/// Paging the catalogue for a client that walks it page by page. The rule that matters is
/// determinism: two products with the same name must not swap places between two requests,
/// or one is listed twice and the other never.
/// </summary>
public class ProductPagingTests
{
    private static Product Named(string sku, string nameFr, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Sku = sku,
        NameFr = nameFr,
        NameAr = "",
        NameEn = "",
    };

    /// <summary>Every name the same, arrival order shuffled: the worst case for paging.</summary>
    private static IQueryable<Product> Ties(int count) =>
        Enumerable.Range(0, count)
            .Select(i => Named($"S{i:000}", "Collier"))
            .OrderBy(_ => Guid.NewGuid())
            .ToList()
            .AsQueryable();

    [Fact]
    public void Pages_are_disjoint_and_complete_when_every_name_ties()
    {
        var all = Ties(7);

        var walked = new List<Guid>();
        for (var skip = 0; skip < 7; skip += 3)
            walked.AddRange(ProductPaging.Page(all, skip, 3).Select(p => p.Id));

        Assert.Equal(7, walked.Count);
        Assert.Equal(7, walked.Distinct().Count());
        Assert.Equal(all.Select(p => p.Id).OrderBy(id => id), walked.OrderBy(id => id));
    }

    [Fact]
    public void Take_is_clamped_to_what_one_response_may_carry()
    {
        var all = Ties(ProductPaging.MaxTake + 5);

        Assert.Equal(ProductPaging.MaxTake, ProductPaging.Page(all, 0, 999_999).Count());
        Assert.Single(ProductPaging.Page(all, 0, 0));
        Assert.Single(ProductPaging.Page(all, 0, -4));
    }

    [Fact]
    public void A_negative_skip_starts_at_the_beginning()
    {
        var all = Ties(3);

        Assert.Equal(
            ProductPaging.Page(all, 0, 2).Select(p => p.Id),
            ProductPaging.Page(all, -10, 2).Select(p => p.Id));
    }

    [Fact]
    public void Without_a_take_the_old_cap_applies_and_nothing_is_skipped()
    {
        // The callers that predate paging ask for the list and get the list, skip or no skip.
        var all = Ties(4);

        Assert.Equal(4, ProductPaging.Page(all, 3, null).Count());
        Assert.Equal(4, ProductPaging.Page(all, null, null).Count());
    }

    [Fact]
    public void Order_is_by_French_name_and_then_by_id()
    {
        var second = Named("B", "Alpha", Guid.Parse("00000000-0000-0000-0000-000000000002"));
        var first = Named("A", "Alpha", Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var last = Named("Z", "Zinc", Guid.Parse("00000000-0000-0000-0000-000000000003"));

        var ordered = ProductPaging.Page(new[] { last, second, first }.AsQueryable(), 0, 10)
            .Select(p => p.Sku)
            .ToList();

        Assert.Equal(new[] { "A", "B", "Z" }, ordered);
    }
}
