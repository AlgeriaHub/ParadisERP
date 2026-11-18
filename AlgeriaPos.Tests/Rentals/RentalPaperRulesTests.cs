using AlgeriaPos.Application.Rentals;

namespace AlgeriaPos.Tests.Rentals;

/// <summary>
/// The paperwork rules the owner's answers of 2026-09-30 added, asked of the engine alone: the
/// signatory's identity (10), the driver and the truck (16), the replacement value per item (13)
/// and the site (9).
/// </summary>
public class RentalPaperRulesTests
{
    private static readonly SignatoryIdentity Complete =
        new("nationalCard", "109 876 543", "Daïra de Bab El Oued", new DateOnly(2019, 5, 12), new DateOnly(1980, 3, 4));

    [Fact]
    public void A_complete_identity_is_missing_nothing()
    {
        Assert.Empty(RentalRules.SignatoryIdentityMissing(locked: false, required: true, Complete));
    }

    [Fact]
    public void Only_the_document_and_its_number_are_required_since_the_answer_of_2026_10_02()
    {
        var blank = new SignatoryIdentity(null, " ", "", null, null);
        Assert.Equal(["idType", "idNumber"], RentalRules.SignatoryIdentityMissing(locked: false, required: true, blank));
        // Issuer, issue date and birth date may stay empty.
        Assert.Empty(RentalRules.SignatoryIdentityMissing(false, true,
            Complete with { IdIssuer = null, IdIssuedOn = null, BirthDate = null }));
    }

    [Fact]
    public void The_identity_rule_bites_once_and_never_on_a_contract_raised_before_it()
    {
        var blank = new SignatoryIdentity(null, null, null, null, null);
        Assert.Empty(RentalRules.SignatoryIdentityMissing(locked: true, required: true, blank));
        Assert.Empty(RentalRules.SignatoryIdentityMissing(locked: false, required: false, blank));
    }

    [Theory]
    [InlineData("nationalCard", true)]
    [InlineData("drivingLicence", true)]
    [InlineData("passport", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Passport", false)]
    [InlineData("carteVitale", false)]
    public void Only_the_three_papers_the_contract_lists_are_accepted(string? type, bool accepted) =>
        Assert.Equal(accepted, RentalRules.IdTypeProblem(type) is null);

    [Fact]
    public void A_note_needs_the_driver_his_phone_and_the_truck()
    {
        Assert.Empty(RentalRules.TransportMissing("Rachid", "0555", "01234-116-16"));
        Assert.Equal(["driverName", "driverPhone", "truckNumber"], RentalRules.TransportMissing(null, " ", ""));
        Assert.Equal(["driverPhone"], RentalRules.TransportMissing("Rachid", null, "01234-116-16"));
    }

    [Fact]
    public void A_line_starts_from_the_item_s_replacement_value_and_a_typed_price_wins()
    {
        Assert.Equal(3000m, RentalRules.DamagePriceFor(0m, 3000m));
        Assert.Equal(2500m, RentalRules.DamagePriceFor(2500m, 3000m));
        Assert.Equal(0m, RentalRules.DamagePriceFor(0m, null));
        Assert.Equal(0m, RentalRules.DamagePriceFor(0m, 0m));
        Assert.Equal("negativeDamageValue", RentalRules.DamageValueProblem(-1m));
        Assert.Null(RentalRules.DamageValueProblem(null));
        Assert.Null(RentalRules.DamageValueProblem(0m));
    }

    [Fact]
    public void A_site_needs_a_name_and_nothing_else()
    {
        Assert.Null(CustomerSiteRules.Problem("Chantier 120 logements", null, null));
        Assert.Equal("siteNameRequired", CustomerSiteRules.Problem("  ", "Rue 1", null));
        Assert.Equal("siteNameTooLong", CustomerSiteRules.Problem(new string('x', 121), null, null));
        Assert.Equal("siteAddressTooLong", CustomerSiteRules.Problem("A", new string('x', 301), null));
        Assert.Equal("sitePhoneTooLong", CustomerSiteRules.Problem("A", null, new string('9', 41)));
    }
}
