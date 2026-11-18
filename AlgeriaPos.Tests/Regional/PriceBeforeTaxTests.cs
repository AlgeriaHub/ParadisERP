using AlgeriaPos.Application.Regional;

namespace AlgeriaPos.Tests.Regional;

/// <summary>
/// Which side of the shelf price the tax sits on.
///
/// <para>
/// The figure on a product, a card and a contract is the price the company sells at. The tax
/// is charged on top of it. Read the other way round — as a price the tax is already inside —
/// every base silently shrinks by the rate and the company invoices less than it quoted, while
/// the customer pays exactly what they were shown and nothing looks wrong anywhere. That is
/// why this is a test and not a comment: it was the other way round once, and on a 25,500 DA
/// ticket the difference was 4,845 DA that nobody would have gone looking for.
/// </para>
/// </summary>
public class PriceBeforeTaxTests
{
    [Fact]
    public void Algeria_quotes_before_tax_and_charges_it_on_top()
    {
        var dz = RegionalPresets.Create("DZ")!;

        Assert.False(dz.PricesIncludeTax);
    }

    [Fact]
    public void The_tax_has_the_same_short_name_in_all_three_languages()
    {
        // The till's tick, the receipt's tax line and the invoice column all read this one
        // value. A long name in one language and an abbreviation in the other two makes the
        // same control read differently depending on who is standing at the terminal.
        var dz = RegionalPresets.Create("DZ")!;

        Assert.Equal("TVA", dz.TaxNames?["fr"]);
        Assert.Equal("VAT", dz.TaxNames?["en"]);
        Assert.Equal("ض.م", dz.TaxNames?["ar"]);
    }
}
