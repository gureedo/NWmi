using System.Globalization;

namespace NWmi.Wmi;

/// <summary>
/// The DMTF datetime formats WMI carries a CIM datetime in: a moment is <c>yyyymmddHHMMSS.mmmmmm+UUU</c> (UUU is the
/// offset from UTC in minutes) and a length of time is <c>ddddddddHHMMSS.mmmmmm:000</c>. Both are 25 characters.
/// </summary>
internal static class DmtfDateTime
{
    private const int Length = 25;
    private const int DateTimePartLength = 21; // yyyyMMddHHmmss.ffffff
    private const int SeparatorIndex = 14; // the '.' before the microseconds
    private const int IntervalDaysLength = 8;
    private const int IntervalTimeLength = 13; // HHmmss.ffffff
    private const string IntervalSuffix = ":000";
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    /// <summary>A moment as a DMTF datetime.</summary>
    public static string Format(DateTimeOffset value)
    {
        var offset = (int)value.Offset.TotalMinutes;
        var microseconds = value.Ticks % TimeSpan.TicksPerSecond / TicksPerMicrosecond;
        return string.Create(CultureInfo.InvariantCulture,
            $"{value:yyyyMMddHHmmss}.{microseconds:000000}{(offset < 0 ? '-' : '+')}{Math.Abs(offset):000}");
    }

    /// <summary>A length of time as a DMTF interval; it cannot be negative.</summary>
    public static string Format(TimeSpan value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.Zero);
        var microseconds = value.Ticks % TimeSpan.TicksPerSecond / TicksPerMicrosecond;
        return string.Create(CultureInfo.InvariantCulture,
            $"{value.Days:00000000}{value.Hours:00}{value.Minutes:00}{value.Seconds:00}.{microseconds:000000}{IntervalSuffix}");
    }

    /// <summary>
    /// A DMTF datetime as a <see cref="DateTimeOffset"/>, or a DMTF interval as a <see cref="TimeSpan"/>. Anything else, such as a
    /// datetime with wildcards, comes back as the same string.
    /// </summary>
    public static object Parse(string text)
    {
        if (text.Length != Length || text[SeparatorIndex] != '.')
            return text;

        if (text.EndsWith(IntervalSuffix, StringComparison.Ordinal)
            && int.TryParse(text.AsSpan(0, IntervalDaysLength), NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            && TimeSpan.TryParseExact(text.AsSpan(IntervalDaysLength, IntervalTimeLength), @"hhmmss\.ffffff",
                CultureInfo.InvariantCulture, out var time))
            return TimeSpan.FromDays(days) + time;

        return DateTime.TryParseExact(text.AsSpan(0, DateTimePartLength), "yyyyMMddHHmmss.ffffff",
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
               && int.TryParse(text.AsSpan(DateTimePartLength), NumberStyles.AllowLeadingSign,
                   CultureInfo.InvariantCulture, out var minutes)
            ? new DateTimeOffset(local, TimeSpan.FromMinutes(minutes))
            : text;
    }
}
