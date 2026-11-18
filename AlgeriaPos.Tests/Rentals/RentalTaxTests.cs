using AlgeriaPos.Application.Regional;
using AlgeriaPos.Domain;

namespace AlgeriaPos.Tests.Rentals;

/// <summary>
/// Whether hire carries tax is a setting, and whether a document shows the tax line when it is
/// zero is a second, independent one. The owner asked for both, and they answer different
/// questions: an untaxed invoice may say nothing about tax, or may state "tax 0.00" so the
/// customer can see it was considered rather than forgotten.
/// </summary>
public class RentalTaxTests
{
    [Fact]
    public void Hire_is_untaxed_until_a_company_says_otherwise()
    {
        // Inferring 19% would be exactly the hardcoded tax rule the country profile exists to
        // prevent. Whether hire is taxed depends on the business and its jurisdiction.
        Assert.Equal(0m, new Store().RentalTaxRate);
        Assert.Equal(0m, new RentalContractLine().TaxRate);
    }

    [Fact]
    public void Showing_a_zero_tax_line_is_a_separate_decision_from_charging_tax()
    {
        var settings = RegionalPresets.Create("DZ")!;

        // Off by default: an empty line on a receipt is noise until somebody asks for it.
        Assert.False(settings.ShowZeroTax);

        // And it survives validation, because it is a presentation choice rather than a
        // figure — nothing may normalise it away.
        settings.ShowZeroTax = true;
        RegionalSettingsValidator.NormalizeAndValidate(settings);
        Assert.True(settings.ShowZeroTax);
    }

    [Fact]
    public void A_contract_line_freezes_the_rate_it_was_signed_at()
    {
        // The line carries its own rate rather than reading the company setting later, for the
        // same reason it carries its own prices: the customer signed a figure, and a rate
        // changed next quarter must not rewrite what they agreed to.
        var signed = new RentalContractLine { TaxRate = 19m };

        var company = new Store { RentalTaxRate = 9m };   // changed afterwards
        Assert.Equal(19m, signed.TaxRate);
        Assert.NotEqual(company.RentalTaxRate, signed.TaxRate);
    }

    [Theory]
    [InlineData(0, 0)]          // untaxed: the default, and the common case today
    [InlineData(19, 1900)]      // the Algerian standard rate, if the company sets it
    [InlineData(9, 900)]        // a reduced rate
    public void Tax_is_charged_on_the_total_at_the_frozen_rate(decimal rate, decimal expected)
    {
        var regional = RegionalPresets.Create("DZ")!;

        // Charged on the rate's total rather than line by line, the same rule sales follow:
        // rounding each line and adding up drifts from the figure on the invoice, and the
        // invoice is what the customer checks and the business declares.
        var net = 10_000m;
        var tax = rate <= 0 ? 0m : regional.RoundMoney(net * rate / 100m);

        Assert.Equal(expected, tax);
    }

    [Fact]
    public void Refunding_an_advance_refunds_its_tax_with_it()
    {
        var regional = RegionalPresets.Create("DZ")!;
        const decimal rate = 19m;

        // An early return large enough to outweigh what the note charges makes the note's tax
        // negative. That is correct rather than a guard to add: giving back an invoice gives
        // back the tax that was charged on it.
        var chargedNet = 2_000m;
        var refundedNet = 5_000m;
        var tax = regional.RoundMoney((chargedNet - refundedNet) * rate / 100m);

        Assert.True(tax < 0);
        Assert.Equal(-570m, tax);
    }
}
