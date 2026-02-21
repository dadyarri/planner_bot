using System.Globalization;

namespace PlannerBot.Services;

/// <summary>
/// Utilities for timezone conversions and datetime operations.
/// Handles conversions between UTC and Moscow time.
/// </summary>
public class TimeZoneUtilities
{
    private static readonly TimeZoneInfo MoscowTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Moscow");
    private static readonly CultureInfo RussianCultureInfo = new("ru-RU");

    /// <summary>
    /// Converts local Moscow time to UTC.
    /// </summary>
    public DateTime ConvertToUtc(DateTime localTime)
    {
        if (localTime.Kind == DateTimeKind.Utc)
            return localTime;
        return TimeZoneInfo.ConvertTimeToUtc(localTime, MoscowTimeZone);
    }

    /// <summary>
    /// Converts UTC time to Moscow time.
    /// </summary>
    public DateTime ConvertToMoscow(DateTime utcTime)
    {
        if (utcTime.Kind != DateTimeKind.Utc)
            utcTime = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTime(utcTime, MoscowTimeZone);
    }

    /// <summary>
    /// Gets current date in Moscow timezone.
    /// </summary>
    public DateTime GetMoscowDate()
    {
        var moscowNow = ConvertToMoscow(DateTime.UtcNow);
        return moscowNow.Date;
    }

    /// <summary>
    /// Gets current date and time in Moscow timezone.
    /// </summary>
    public DateTime GetMoscowDateTime()
    {
        return ConvertToMoscow(DateTime.UtcNow);
    }

    /// <summary>
    /// Gets the Russian culture info for formatting.
    /// </summary>
    public CultureInfo GetRussianCultureInfo() => RussianCultureInfo;
}
