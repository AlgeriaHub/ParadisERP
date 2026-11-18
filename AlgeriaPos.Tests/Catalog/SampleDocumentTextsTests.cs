using AlgeriaPos.Application.Catalog;
using AlgeriaPos.Application.Contracts;

namespace AlgeriaPos.Tests.Catalog;

/// <summary>
/// The wording a company starts from before its own paragraphs exist.
///
/// <para>
/// The rule these tests hold is not about the prose. It is that nothing here can be mistaken for
/// a company's own terms: every sample says on its face that it is one, in the language it is
/// written in, and the marker is part of the stored text — so a contract printed before anyone
/// edited it carries the admission onto the paper. A sample that lost its marker would be
/// indistinguishable from a clause the owner had agreed to.
/// </para>
/// </summary>
public class SampleDocumentTextsTests
{
    [Fact]
    public void Every_printed_kind_has_something_to_start_from_in_all_three_languages()
    {
        var all = SampleDocumentTexts.All();

        Assert.Equal(DocumentTextKinds.All.OrderBy(k => k), all.Keys.OrderBy(k => k));
        foreach (var (kind, byLang) in all)
        {
            Assert.Equal(new[] { "ar", "en", "fr" }, byLang.Keys.OrderBy(l => l));
            foreach (var (lang, text) in byLang)
                Assert.False(string.IsNullOrWhiteSpace(text), $"{kind}/{lang} is blank");
        }
    }

    [Fact]
    public void Every_sample_says_it_is_one_in_its_own_language()
    {
        foreach (var (kind, byLang) in SampleDocumentTexts.All())
        {
            Assert.StartsWith(SampleDocumentTexts.Marker.Ar, byLang["ar"], StringComparison.Ordinal);
            Assert.StartsWith(SampleDocumentTexts.Marker.Fr, byLang["fr"], StringComparison.Ordinal);
            Assert.StartsWith(SampleDocumentTexts.Marker.En, byLang["en"], StringComparison.Ordinal);
            foreach (var text in byLang.Values) Assert.True(SampleDocumentTexts.IsSample(text), kind);
        }

        // And the owner's own wording is not mistaken for one.
        Assert.False(SampleDocumentTexts.IsSample("الدفع خلال 30 يوماً."));
        Assert.False(SampleDocumentTexts.IsSample(null));
        Assert.False(SampleDocumentTexts.IsSample("   "));
    }

    [Fact]
    public void The_whole_sample_is_something_the_documents_route_would_accept()
    {
        // If it were not, the screen would offer a button whose result cannot be saved.
        var problem = StoreDocumentTexts.TryNormalize(SampleDocumentTexts.Dto(), out var normalized);

        Assert.Null(problem);
        Assert.NotNull(normalized.Texts);
        Assert.Equal(DocumentTextKinds.All.Count, normalized.Texts!.Count);
        Assert.Equal(SampleDocumentTexts.SampleEmail, normalized.Email);
    }

    [Fact]
    public void Each_call_hands_back_its_own_copy()
    {
        // The screen edits what it is given; a shared dictionary would leak one admin's edit
        // into the next company's sample.
        var first = SampleDocumentTexts.All();
        first[DocumentTextKinds.RentalContract]["fr"] = "edited";

        Assert.NotEqual("edited", SampleDocumentTexts.All()[DocumentTextKinds.RentalContract]["fr"]);
    }
}
