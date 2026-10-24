using System.Globalization;
using System.Text.Json;

namespace WhatDidIDo.Tray.Core.App;

public sealed record FocusWindow(string AppId, string AppName, string Title);

public sealed record DaySegment(DateTimeOffset Start, DateTimeOffset End, string State, FocusWindow? Window);

public sealed record DayAssignment(string Ticket, string Client, string Category);

public sealed record DayEntry(DayAssignment Assignment, DateTimeOffset ClipStart, DateTimeOffset ClipEnd);

/// <summary>A run of one state; <see cref="Start"/>/<see cref="End"/> are sent back verbatim in an Assign.</summary>
public sealed record DayRun(string Start, string End, string State)
{
    public DateTimeOffset StartAt => TimesheetDay.Instant(Start);
    public DateTimeOffset EndAt => TimesheetDay.Instant(End);
}

public sealed record TicketDefault(string Ticket, string Client, string Category, bool Billable);

/// <summary>
/// <c>GET /api/v1/timesheet/day?date=</c> (whatdidido TIMESHEET.md 4.1), as far
/// as the flyout reads it. Parsing is strict about what it reads (the
/// extension's <c>isDay</c>): a malformed day is an error, not a half-drawn card.
/// </summary>
public sealed record TimesheetDay(
    string Date,
    string Zone,
    DateTimeOffset Now,
    (DateTimeOffset Start, DateTimeOffset End)? Span,
    IReadOnlyList<DaySegment> Segments,
    IReadOnlyList<DayEntry> Entries,
    IReadOnlyList<DayRun> Unassigned,
    IReadOnlyList<string> Tickets,
    IReadOnlyList<TicketDefault> TicketDefaults)
{
    internal static DateTimeOffset Instant(string iso) =>
        DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <exception cref="AppException">Code "invalid": not a timesheet day.</exception>
    public static TimesheetDay Parse(JsonElement root)
    {
        try
        {
            var span = root.GetProperty("span");
            var suggestions = root.TryGetProperty("suggestions", out var s) && s.ValueKind == JsonValueKind.Object ? s : (JsonElement?)null;
            return new TimesheetDay(
                Text(root, "date"),
                Text(root, "zone"),
                Instant(Text(root, "now")),
                span.ValueKind == JsonValueKind.Null ? null : (Instant(Text(span, "start")), Instant(Text(span, "end"))),
                Array(root, "segments").Select(x => new DaySegment(
                    Instant(Text(x, "start")), Instant(Text(x, "end")), Text(x, "state"),
                    x.TryGetProperty("window", out var w) && w.ValueKind == JsonValueKind.Object
                        ? new FocusWindow(Opt(w, "app_id"), Opt(w, "app_name"), Opt(w, "title"))
                        : null)).ToList(),
                Array(root, "entries").Select(x =>
                {
                    var a = x.GetProperty("assignment");
                    return new DayEntry(new DayAssignment(Opt(a, "ticket"), Opt(a, "client"), Opt(a, "category")),
                        Instant(Text(x, "clip_start")), Instant(Text(x, "clip_end")));
                }).ToList(),
                Array(root, "unassigned").Select(x => new DayRun(Text(x, "start"), Text(x, "end"), Text(x, "state"))).ToList(),
                suggestions is { } t1 && t1.TryGetProperty("tickets", out var tk) && tk.ValueKind == JsonValueKind.Array
                    ? tk.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
                    : [],
                suggestions is { } t2 && t2.TryGetProperty("ticket_defaults", out var td) && td.ValueKind == JsonValueKind.Array
                    ? td.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.Object && v.TryGetProperty("ticket", out _))
                        .Select(v => new TicketDefault(Opt(v, "ticket"), Opt(v, "client"), Opt(v, "category"),
                            !v.TryGetProperty("billable", out var b) || b.ValueKind != JsonValueKind.False))
                        .ToList()
                    : []);
        }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new AppException("invalid timesheet day", code: "invalid");
        }
    }

    private static string Text(JsonElement o, string key) =>
        o.GetProperty(key) is { ValueKind: JsonValueKind.String } v ? v.GetString()! : throw new FormatException(key);

    private static string Opt(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static JsonElement.ArrayEnumerator Array(JsonElement o, string key) =>
        o.GetProperty(key) is { ValueKind: JsonValueKind.Array } v ? v.EnumerateArray() : throw new FormatException(key);
}
