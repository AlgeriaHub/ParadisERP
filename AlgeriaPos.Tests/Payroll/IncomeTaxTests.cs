using AlgeriaPos.Application.Payroll;
using AlgeriaPos.Application.Regional;

namespace AlgeriaPos.Tests.Payroll;

/// <summary>
/// The walk over a progressive scale.
///
/// <para>
/// Every bracket here is invented — 10 % to 1,000, then 20 %, then 50 %. That is deliberate and
/// permanent: a test is the easiest place for a "real" scale to be written from memory and then
/// quoted back as authority. The real one is a setting an accountant types, and it never appears
/// in this repository.
/// </para>
/// </summary>
public class IncomeTaxTests
{
    /// <summary>10 % up to 1,000, 20 % up to 2,000, 50 % above. Obviously not anybody's law.</summary>
    private static List<TaxBracket> Synthetic() =>
    [
        new() { UpTo = 1000m, Rate = 10m },
        new() { UpTo = 2000m, Rate = 20m },
        new() { Rate = 50m }
    ];

    [Fact]
    public void Each_slice_is_taxed_at_its_own_rate_and_not_the_whole_amount_at_the_top_one()
    {
        // 1,500 → 1,000 at 10 % = 100, then 500 at 20 % = 100.
        Assert.Equal(200m, IncomeTax.Progressive(1500m, Synthetic()));

        // 3,000 → 100 + 200 + 1,000 at 50 % = 800.
        Assert.Equal(800m, IncomeTax.Progressive(3000m, Synthetic()));
    }

    [Fact]
    public void A_base_inside_the_first_bracket_pays_only_that_brackets_rate()
    {
        Assert.Equal(40m, IncomeTax.Progressive(400m, Synthetic()));
    }

    [Fact]
    public void The_open_top_bracket_takes_everything_above_the_last_ceiling()
    {
        var one = IncomeTax.Progressive(10_000m, Synthetic());
        var two = IncomeTax.Progressive(20_000m, Synthetic());

        // Ten thousand more, all of it in the top step: exactly half of it in tax.
        Assert.Equal(5000m, two - one);
    }

    [Fact]
    public void A_base_at_or_below_zero_is_taxed_nothing()
    {
        // Arithmetic, not policy: there is no slice to tax. It is not the same statement as an
        // unconfigured scale, which the engine refuses before anything reaches here.
        Assert.Equal(0m, IncomeTax.Progressive(0m, Synthetic()));
        Assert.Equal(0m, IncomeTax.Progressive(-500m, Synthetic()));
    }

    [Fact]
    public void An_exact_bracket_boundary_is_not_taxed_twice()
    {
        // 1,000 sits on the first ceiling: 100, and not a centime of the second rate.
        Assert.Equal(100m, IncomeTax.Progressive(1000m, Synthetic()));
        Assert.Equal(300m, IncomeTax.Progressive(2000m, Synthetic()));
    }

    [Fact]
    public void A_first_bracket_at_zero_percent_is_an_exemption_and_not_a_missing_scale()
    {
        List<TaxBracket> withThreshold = [new() { UpTo = 1000m, Rate = 0m }, new() { Rate = 20m }];

        Assert.Equal(0m, IncomeTax.Progressive(1000m, withThreshold));
        Assert.Equal(100m, IncomeTax.Progressive(1500m, withThreshold));
    }

    [Fact]
    public void Rounding_happens_once_at_the_end_and_follows_the_currencys_decimals()
    {
        var regional = new RegionalSettings { CurrencyDecimals = 2 };
        List<TaxBracket> odd = [new() { UpTo = 1000m, Rate = 7m }, new() { Rate = 13m }];

        // 1,000 × 7 % = 70, plus 333 × 13 % = 43.29. Rounded once, here — never twice, and never
        // to two places because two is what somebody assumed.
        Assert.Equal(113.29m, IncomeTax.Progressive(regional, 1333m, odd));

        var whole = new RegionalSettings { CurrencyDecimals = 0 };
        Assert.Equal(113m, IncomeTax.Progressive(whole, 1333m, odd));
    }

    [Fact]
    public void An_empty_or_missing_scale_yields_nothing_rather_than_throwing()
    {
        // The guard against "empty means no tax" lives in the engine's refusal, not here: this
        // function is arithmetic and is never reached while the mode is NotConfigured.
        Assert.Equal(0m, IncomeTax.Progressive(5000m, []));
        Assert.Equal(0m, IncomeTax.Progressive(5000m, null));
    }
}
