using System.Globalization;
using WhatDidIDo.Tray.Core.App;

namespace WhatDidIDo.Tray.Core.Models;

/// <summary>A Today bar's bucket: its active share (0..1), dominant state, and the ticket covering its middle.</summary>
/// <param name="Ticket">"" for an entry without a ticket, null when no entry covers the middle.</param>
public sealed record SparkBucket(DateTimeOffset Start, DateTimeOffset End, double Active, string State, string? Ticket);

/// <summary>
/// Pure helpers over the app server's timesheet day, ported from the
/// extension's lib/day_model.js, itself from the app (core/day.ts,
/// core/time.ts, theme/palette.ts). Colors are the app's <c>win</c> skin
/// (frontend/src/lib/theme/tokens.css).
/// </summary>
public static class DayModel
{
    /// <summary>
    /// The unassigned stretch that runs up to now: the latest active or idle
    /// run that ended at most <paramref name="slackMs"/> before <c>day.now</c>.
    /// </summary>
    public static DayRun? CurrentStretch(TimesheetDay day, long slackMs = 60_000)
    {
        ArgumentNullException.ThrowIfNull(day);
        for (var i = day.Unassigned.Count - 1; i >= 0; i--)
        {
            var r = day.Unassigned[i];
            if ((day.Now - r.EndAt).TotalMilliseconds > slackMs)
                return null;
            if (r.State is "active" or "idle")
                return r;
        }
        return null;
    }

    /// <summary>The entry that ends last (the Now card's "&lt;ticket&gt; until &lt;end&gt;").</summary>
    public static DayEntry? LastEntry(TimesheetDay day)
    {
        ArgumentNullException.ThrowIfNull(day);
        DayEntry? best = null;
        foreach (var e in day.Entries)
        {
            if (best is null || e.ClipEnd > best.ClipEnd)
                best = e;
        }
        return best;
    }

    /// <summary>The most recent segment with a focused window.</summary>
    public static DaySegment? LastFocus(TimesheetDay day)
    {
        ArgumentNullException.ThrowIfNull(day);
        for (var i = day.Segments.Count - 1; i >= 0; i--)
        {
            if (day.Segments[i].Window is not null)
                return day.Segments[i];
        }
        return null;
    }

    /// <summary>
    /// Buckets of <paramref name="minutes"/> from the start of the span (floored
    /// to the bucket size, in UTC like the app) to now; buckets without any
    /// segment are left out. The dominant state's ties go to the first seen.
    /// </summary>
    public static IReadOnlyList<SparkBucket> SparkBuckets(TimesheetDay day, int minutes = 15)
    {
        ArgumentNullException.ThrowIfNull(day);
        if (day.Span is not { } span)
            return [];
        var step = minutes * 60_000L;
        var end = Math.Min(span.End.ToUnixTimeMilliseconds(), day.Now.ToUnixTimeMilliseconds());
        var first = (long)Math.Floor(span.Start.ToUnixTimeMilliseconds() / (double)step) * step;
        var output = new List<SparkBucket>();
        for (var t = first; t < end; t += step)
        {
            // Insertion order matters for ties, so a list of pairs, not a dictionary.
            var per = new List<(string State, long Ms)>();
            foreach (var s in day.Segments)
            {
                var o = Math.Min(s.End.ToUnixTimeMilliseconds(), t + step) - Math.Max(s.Start.ToUnixTimeMilliseconds(), t);
                if (o <= 0)
                    continue;
                var at = per.FindIndex(p => p.State == s.State);
                if (at < 0)
                    per.Add((s.State, o));
                else
                    per[at] = (s.State, per[at].Ms + o);
            }
            if (per.Count == 0)
                continue;
            var (state, most) = ("untracked", -1L);
            foreach (var (k, v) in per)
            {
                if (v > most)
                    (state, most) = (k, v);
            }
            var mid = t + step / 2;
            var cover = day.Entries.FirstOrDefault(e => e.ClipStart.ToUnixTimeMilliseconds() <= mid && mid < e.ClipEnd.ToUnixTimeMilliseconds());
            var active = per.FirstOrDefault(p => p.State == "active").Ms;
            output.Add(new SparkBucket(
                DateTimeOffset.FromUnixTimeMilliseconds(t),
                DateTimeOffset.FromUnixTimeMilliseconds(t + step),
                active / (double)step,
                state,
                cover?.Assignment.Ticket));
        }
        return output;
    }

    /// <summary>Initials for an app tile: "ConnectWise Manage" → "CM", "firefox" → "Fi", "" → "?".</summary>
    public static string AppInitials(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var words = name.Split([' ', '\t', '\n', '\r', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2)
            return string.Concat(words[0][0], words[1][0]).ToUpperInvariant();
        var w = words.Length == 1 ? words[0] : "?";
        w = w.Length > 2 ? w[..2] : w;
        return char.ToUpperInvariant(w[0]) + w[1..];
    }

    // The app's win skin data palette: :root[data-platform="win"] --data-0..7,
    // and the dark theme's; --data-neutral is the root's (the win skin keeps it).
    private static readonly string[] LightColors = ["#c239b3", "#8764b8", "#ca5010", "#498205", "#038387", "#0078d4", "#986f0b", "#00a19a"];
    private static readonly string[] DarkColors = ["#ee5fb7", "#b4a0ff", "#f7894a", "#8cbd18", "#00b7c3", "#4cc2ff", "#d6a700", "#00c8bd"];
    public const string NeutralLight = "#a3a8b3";
    public const string NeutralDark = "#7d828d";
    public const int PaletteSize = 8;

    /// <summary>"#20591", "20591" and " #20591 " share a color.</summary>
    public static string NormalizeTicket(string ticket)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var t = ticket.Trim();
        return (t.StartsWith('#') ? t[1..] : t).ToLowerInvariant();
    }

    /// <summary>FNV-1a over the normalized string's code points plus a murmur3 finalizer (the app's paletteSlot).</summary>
    public static int PaletteSlot(string key)
    {
        var h = 0x811c9dc5u;
        foreach (var rune in NormalizeTicket(key).EnumerateRunes())
        {
            h ^= (uint)rune.Value;
            h = unchecked(h * 0x01000193u);
        }
        h ^= h >> 16;
        h = unchecked(h * 0x85ebca6bu);
        h ^= h >> 13;
        h = unchecked(h * 0xc2b2ae35u);
        h ^= h >> 16;
        return (int)(h % PaletteSize);
    }

    /// <summary>"#rrggbb" of a ticket; empty tickets (admin or internal time) are neutral.</summary>
    public static string TicketColor(string? ticket, bool dark)
    {
        if (string.IsNullOrEmpty(ticket) || NormalizeTicket(ticket).Length == 0)
            return dark ? NeutralDark : NeutralLight;
        return (dark ? DarkColors : LightColors)[PaletteSlot(ticket)];
    }

    /// <summary>An IANA zone (day.zone); an unknown or empty one is this machine's zone, like the app.</summary>
    public static TimeZoneInfo Zone(string? zone)
    {
        if (!string.IsNullOrEmpty(zone) && TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var tz))
            return tz;
        return TimeZoneInfo.Local;
    }

    /// <summary>Wall-clock time in the day's zone: "5:14 PM" (12-hour) or "17:14" (24-hour).</summary>
    public static string ClockTime(DateTimeOffset at, string? zone, bool use24h)
    {
        var local = TimeZoneInfo.ConvertTime(at, Zone(zone));
        return local.ToString(use24h ? "HH:mm" : "h:mm tt", CultureInfo.InvariantCulture);
    }

    /// <summary>"HH:MM" (24-hour) in the day's zone, for <c>?t=</c> deep links.</summary>
    public static string Hhmm(DateTimeOffset at, string? zone) => ClockTime(at, zone, use24h: true);

    /// <summary>The local date (YYYY-MM-DD) on this machine, for <c>?date=</c> without a day.</summary>
    public static string LocalDate(DateTimeOffset at) =>
        at.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Whether Windows' short time format is 24-hour (Settings → Time &amp; language → Region).</summary>
    public static bool Uses24Hour(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return culture.DateTimeFormat.ShortTimePattern.Contains('H', StringComparison.Ordinal);
    }
}
