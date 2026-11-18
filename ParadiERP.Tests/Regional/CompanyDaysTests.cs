using ParadiERP.Application.Regional;

namespace ParadiERP.Tests.Regional;

public class CompanyDaysTests
{
    private static readonly TimeZoneInfo Algiers = GetAlgiersZone();

    private static TimeZoneInfo GetAlgiersZone()
    {
        var z = CompanyDays.Zone("Africa/Algiers");
        if (z.BaseUtcOffset == TimeSpan.FromHours(1) && !z.SupportsDaylightSavingTime) return z;
        try
        {
            var win = TimeZoneInfo.FindSystemTimeZoneById("W. Central Africa Standard Time");
            if (win.BaseUtcOffset == TimeSpan.FromHours(1)) return win;
        }
        catch { }
        return TimeZoneInfo.CreateCustomTimeZone("dz", TimeSpan.FromHours(1), "Algiers", "Algiers");
    }

    [Fact]
    public void TryParseDay_accepts_plain_date_and_refuses_instants_and_slashes()
    {
        Assert.True(CompanyDays.TryParseDay("2026-10-01", out var day));
        Assert.Equal(new DateOnly(2026, 10, 1), day);

        Assert.False(CompanyDays.TryParseDay("2026-10-01T00:00:00Z", out _));
        Assert.False(CompanyDays.TryParseDay("01/10/2026", out _));
        Assert.False(CompanyDays.TryParseDay(null, out _));
        Assert.False(CompanyDays.TryParseDay("", out _));
    }

    [Fact]
    public void StartUtc_in_Algiers_is_one_hour_before_midnight_UTC()
    {
        var start = CompanyDays.StartUtc(new DateOnly(2026, 10, 1), Algiers);
        Assert.Equal(new DateTime(2026, 9, 30, 23, 0, 0, DateTimeKind.Utc), start);
    }

    [Fact]
    public void Zone_falls_back_to_UTC_for_unknown_time_zone()
    {
        Assert.Equal(TimeZoneInfo.Utc, CompanyDays.Zone("Not/AZone"));
        Assert.Equal(TimeZoneInfo.Utc, CompanyDays.Zone(null));
        Assert.Equal(TimeZoneInfo.Utc, CompanyDays.Zone(""));
    }

    [Fact]
    public void Today_in_Algiers_advances_at_23_00_UTC()
    {
        var utc = new DateTime(2026, 10, 11, 23, 30, 0, DateTimeKind.Utc);
        Assert.Equal(new DateOnly(2026, 10, 12), CompanyDays.Today(utc, Algiers));
    }
}
