using System.Globalization;

namespace ParadisERP.Application.Regional;

/// <summary>
/// Plain dates on the wire, read in the company's own time zone. A screen sends "2026-10-01"; the
/// server decides which instant that day starts at. Sending the browser's midnight instead made a
/// statement opened from another zone (or a machine with the wrong clock zone) start an hour off.
/// </summary>
public static class CompanyDays
{
    /// <summary>The company's zone; one this machine does not know falls back to UTC, as the receipt clock does.</summary>
    public static TimeZoneInfo Zone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }

    /// <summary>A plain date, <c>yyyy-MM-dd</c> and nothing else.</summary>
    public static bool TryParseDay(string? raw, out DateOnly day) =>
        DateOnly.TryParseExact(raw?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    /// <summary>The instant the company's day starts, in UTC.</summary>
    public static DateTime StartUtc(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // A day that starts inside a daylight-saving gap starts when the clocks resume.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    /// <summary>Today in the company's zone.</summary>
    public static DateOnly Today(DateTime utcNow, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone));
}
