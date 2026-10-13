using System.Globalization;
using System.Text.RegularExpressions;
using Rtfd.Config;

namespace Rtfd.Agents;

/// <param name="ResetAt">When the account can run again.</param>
/// <param name="Message">The text that matched, for the log and <c>status</c>.</param>
public sealed record LimitHit(DateTimeOffset ResetAt, string Message);

/// <summary>
/// Spots a usage-limit message in an agent's output and works out when the limit resets
/// (spec "Agent adapter", outcome <c>limit</c>). A pure function of the text, the patterns, the
/// current time and the local zone, so the spec's cases are a table test.
/// </summary>
/// <remarks>
/// A pattern may name groups: <c>reset</c> (a time such as <c>12:10am</c>), <c>date</c> (such as
/// <c>Oct 14</c>, for weekly limits) and <c>zone</c> (an IANA zone such as
/// <c>Europe/Chisinau</c>). A time already past today means tomorrow; no time at all means a
/// 30-minute pause.
/// </remarks>
public static partial class LimitParser
{
    public static readonly TimeSpan DefaultPause = TimeSpan.FromMinutes(30);

    public static LimitHit? Parse(string text, IReadOnlyList<ConfigPattern> patterns, DateTimeOffset now, TimeZoneInfo localZone)
    {
        foreach (var pattern in patterns)
        {
            var match = new Regex(pattern.Pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant).Match(text);
            if (!match.Success)
            {
                continue;
            }

            var message = match.Value.Trim();
            var reset = match.Groups["reset"];
            if (!reset.Success || ParseTime(reset.Value) is not { } time)
            {
                return new LimitHit(now + DefaultPause, message);
            }

            var zone = match.Groups["zone"] is { Success: true } z ? FindZone(z.Value.Trim()) ?? localZone : localZone;
            var today = TimeZoneInfo.ConvertTime(now, zone).Date;
            var date = match.Groups["date"] is { Success: true } d ? ParseDate(d.Value, today) : null;

            var resetAt = ToInstant((date ?? today) + time, zone);
            if (date is null && resetAt <= now)
            {
                resetAt = ToInstant(today.AddDays(1) + time, zone);
            }

            return new LimitHit(resetAt, message);
        }

        return null;
    }

    /// <summary>"12:10am", "3pm", "11:30 PM". 12am is midnight and 12pm is noon.</summary>
    internal static TimeSpan? ParseTime(string text)
    {
        var match = Time().Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }

        var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        if (hour is < 1 or > 12 || minute > 59)
        {
            return null;
        }

        var pm = match.Groups["p"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
        hour = (hour % 12) + (pm ? 12 : 0);
        return new TimeSpan(hour, minute, 0);
    }

    /// <summary>"Oct 14" or "Oct 14, ", in the year that puts it nearest to today.</summary>
    internal static DateTime? ParseDate(string text, DateTime today)
    {
        var match = Date().Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }

        var month = Array.FindIndex(CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames,
            m => m.Length > 0 && m.Equals(match.Groups["mon"].Value, StringComparison.OrdinalIgnoreCase)) + 1;
        var day = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);
        if (month < 1 || day > DateTime.DaysInMonth(today.Year, month))
        {
            return null;
        }

        var date = new DateTime(today.Year, month, day, 0, 0, 0, DateTimeKind.Unspecified);

        // A reset date in January seen in late December is next year's.
        return date < today.AddMonths(-6) ? date.AddYears(1) : date;
    }

    /// <summary>IANA names work on Windows too, through ICU (so InvariantGlobalization stays off).</summary>
    internal static TimeZoneInfo? FindZone(string name)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(name);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return null;
        }
    }

    private static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        // A time inside a spring-forward gap doesn't exist; the hour after it does.
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    [GeneratedRegex(@"^(?<h>\d{1,2})(:(?<m>\d{2}))?\s*(?<p>[ap]m)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Time();

    [GeneratedRegex(@"^(?<mon>[A-Za-z]{3})\s+(?<d>\d{1,2}),?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Date();
}
