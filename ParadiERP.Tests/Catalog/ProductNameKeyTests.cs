using ParadiERP.Application.Catalog;

namespace ParadiERP.Tests.Catalog;

/// <summary>
/// The spellings are the owner's own, from the Ouled Moussa count of 2026-09-29: the same sheet
/// writes a dimension as <c>0,24*1,00</c> on one line and <c>0.24* 3.00</c> on the next.
/// </summary>
public class ProductNameKeyTests
{
    [Theory]
    [InlineData("بانو 0,24*1,00 م", "بانو 0.24* 1.00 م")]
    [InlineData("بانو 0.24* 1.00 م", "بانو 0.24*1م")]
    [InlineData("بوطو 0.45 *0.45 * 3,00 م", "بوطو 0.45*0.45*3 م")]
    [InlineData("بانو 0.30x1.00 م", "بانو 0.30*1.00 م")]
    [InlineData("بانو 0.30 × 1.00 م", "بانو 0.30*1.00 م")]
    [InlineData(" اكس طويل  ", "اكس طويل")]
    [InlineData("اكس طويل", "اكسطويل")]
    [InlineData("أرجل جالفا", "ارجل جالفا")]
    [InlineData("زاوية", "زاويه")]
    [InlineData("بانو ٠٫٥٠*٣ م", "بانو 0.5*3 م")]
    [InlineData("Échafaudage", "echafaudage")]
    public void Spellings_a_typist_varies_are_the_same_article(string one, string other) =>
        Assert.Equal(ProductNameKey.For(one), ProductNameKey.For(other));

    [Theory]
    [InlineData("بانو 0.50 * 1.00 م", "بانو 0.50 * 1.50 م")]
    [InlineData("اكس طويل", "اكس طويل 3 م")]
    [InlineData("بيدروا 4.00 م", "بيدروا 4.00 م خاسرة")]
    [InlineData("بولون", "بولون 0,19 سم")]
    // Not read as 0.76: a missing point is a typo only a person can confirm.
    [InlineData("بانو 076* 0.50 م", "بانو 0.76* 0.50 م")]
    public void Names_that_differ_in_what_they_say_stay_apart(string one, string other) =>
        Assert.NotEqual(ProductNameKey.For(one), ProductNameKey.For(other));

    [Fact]
    public void The_stored_name_keeps_the_typist_s_words_and_only_tidies_the_spacing()
    {
        Assert.Equal("بانو 0,24*1,00 م", ProductNameKey.Clean("  بانو   0,24*1,00 م "));
        Assert.Equal("", ProductNameKey.Clean("   "));
        Assert.Equal("", ProductNameKey.For(null));
    }
}
