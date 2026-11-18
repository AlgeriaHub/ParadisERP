using System.Globalization;
using AlgeriaPos.Application.Customers;

namespace AlgeriaPos.Tests.Customers;

/// <summary>
/// The owner's answer of 2026-10-02: the issuer, the issue date and the birth date are optional,
/// "but if entered, correct — or empty". Blank is never a problem; a filled part that cannot be one
/// is refused in a sentence.
/// </summary>
public class IdentityProblemTests
{
    private static readonly DateOnly Today = new(2026, 10, 2);

    private static string? Check(string? number = "109876543", string? issuer = null,
        DateOnly? issuedOn = null, DateOnly? birthDate = null, string? type = "nationalCard") =>
        IdDocumentTypes.IdentityProblem(type, number, issuer, issuedOn, birthDate, Today);

    [Fact]
    public void Everything_empty_or_everything_right_is_accepted()
    {
        Assert.Null(IdDocumentTypes.IdentityProblem(null, null, null, null, null, Today));
        Assert.Null(Check(number: "109 876-543/B", issuer: "Daïra de Bab El Oued",
            issuedOn: new DateOnly(2019, 5, 12), birthDate: new DateOnly(1980, 3, 4)));
        Assert.Null(Check(issuer: "دائرة باب الوادي", issuedOn: Today, birthDate: new DateOnly(1980, 3, 4)));
    }

    [Theory]
    [InlineData("Ben Ali")]   // a name in the number box: no digit
    [InlineData("12#34")]     // a character no card prints
    [InlineData("-123")]      // starts with punctuation
    public void A_number_that_is_not_one_is_refused(string number) => Assert.NotNull(Check(number: number));

    [Fact]
    public void An_issuer_of_figures_only_is_refused() => Assert.NotNull(Check(issuer: "16/00"));

    [Fact]
    public void Dates_in_the_future_or_before_1900_are_refused()
    {
        Assert.NotNull(Check(birthDate: Today.AddDays(1)));
        Assert.NotNull(Check(birthDate: new DateOnly(1899, 12, 31)));
        Assert.NotNull(Check(issuedOn: Today.AddDays(1)));
        Assert.NotNull(Check(issuedOn: new DateOnly(1899, 12, 31)));
    }

    [Fact]
    public void The_before_1900_refusal_is_a_sentence_under_any_server_culture()
    {
        // The API runs under the host's culture. Under a Hijri one (ar-SA, Um al-Qura) formatting
        // 1900-01-01 with it threw — the calendar starts in 1900-04 — so the refusal became a 500
        // (found 2026-10-15).
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
        try
        {
            Assert.Equal("The date of birth cannot be before 1900-01-01.", Check(birthDate: new DateOnly(1899, 12, 31)));
            Assert.Equal("The identity document's issue date cannot be before 1900-01-01.", Check(issuedOn: new DateOnly(1899, 12, 31)));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void A_paper_cannot_be_issued_before_its_holder_was_born() =>
        Assert.NotNull(Check(issuedOn: new DateOnly(1979, 1, 1), birthDate: new DateOnly(1980, 3, 4)));

    [Fact]
    public void Today_anywhere_is_never_behind_the_company_clock() =>
        Assert.Equal(new DateOnly(2026, 10, 3), IdDocumentTypes.TodayAnywhere(new DateTime(2026, 10, 2, 23, 30, 0, DateTimeKind.Utc)));
}
