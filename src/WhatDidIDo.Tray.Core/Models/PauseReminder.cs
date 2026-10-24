namespace WhatDidIDo.Tray.Core.Models;

/// <summary>What the caller does after a reminder call: show (replace any shown reminder) and/or withdraw.</summary>
public sealed record ReminderEffect(string? Show, bool Withdraw)
{
    public static readonly ReminderEffect None = new(null, false);
    public static readonly ReminderEffect WithdrawShown = new(null, true);
}

/// <summary>
/// Reminds while the work session stays paused; ported from the extension's
/// lib/pause_reminder.js. Times are milliseconds on a monotonic clock passed in
/// by the caller.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>PausedSince is when "paused" was first observed after anything else
/// (another state, not running, or a fresh start). The server reports no
/// paused-since time, so a pause that began before the tray started counts
/// from the first poll that saw it.</item>
/// <item>The first reminder is due at PausedSince + minutes, each later one
/// minutes after the previous reminder (the time it was actually shown).</item>
/// <item>Leaving "paused" cancels everything and withdraws a shown reminder.</item>
/// <item>SetMinutes while paused recomputes; already overdue reminds at once; 0 cancels and withdraws.</item>
/// <item>Suppress ("Stop reminding") stops reminders until "paused" is left.</item>
/// </list>
/// </remarks>
public sealed class PauseReminder(int minutes = PauseReminder.DefaultMinutes)
{
    public const long MinuteMs = 60_000;
    public const int DefaultMinutes = 10;
    public const int MaxMinutes = 240;
    public const string Title = "Recording is paused";

    private int _minutes = Clamp(minutes);
    private long? _lastReminder;
    private bool _suppressed;

    public int Minutes => _minutes;
    public long? PausedSince { get; private set; }

    /// <summary>When the next reminder is due, or null when none is.</summary>
    public long? DueAt => PausedSince is null || _minutes == 0 || _suppressed
        ? null
        : (_lastReminder ?? PausedSince.Value) + _minutes * MinuteMs;

    public long? PausedFor(long now) => now - PausedSince;

    /// <param name="state">The polled status.state; null when the session is not running.</param>
    public ReminderEffect Observe(string? state, long now)
    {
        if (state != "paused")
        {
            if (PausedSince is null)
                return ReminderEffect.None;
            PausedSince = null;
            _lastReminder = null;
            _suppressed = false;
            return ReminderEffect.WithdrawShown;
        }
        if (PausedSince is null)
        {
            PausedSince = now;
            return ReminderEffect.None;
        }
        // Normally the timer fires first; this catches a missed one.
        return Fire(now);
    }

    public ReminderEffect SetMinutes(int value, long now)
    {
        _minutes = Clamp(value);
        if (PausedSince is null)
            return ReminderEffect.None;
        return _minutes == 0 ? ReminderEffect.WithdrawShown : Fire(now);
    }

    public ReminderEffect Fire(long now)
    {
        if (DueAt is not { } due || now < due)
            return ReminderEffect.None;
        _lastReminder = now;
        return new ReminderEffect(Body(now - PausedSince!.Value), false);
    }

    public ReminderEffect Suppress()
    {
        if (PausedSince is null)
            return ReminderEffect.None;
        _suppressed = true;
        return ReminderEffect.WithdrawShown;
    }

    public static long WholeMinutes(long ms) => ms > 0 ? ms / MinuteMs : 0;

    /// <summary>The status card's subtitle while paused.</summary>
    public static string PausedForText(long ms)
    {
        var m = WholeMinutes(ms);
        return m < 1 ? "Paused for <1 min" : $"Paused for {m} min";
    }

    public static string Body(long ms)
    {
        var m = WholeMinutes(ms);
        return $"Paused for {m} minute{(m == 1 ? "" : "s")}. Screenshots, audio and window activity aren't being recorded.";
    }

    /// <summary>Milliseconds for the timer (0 = at once), or null for no timer.</summary>
    public static long? TimerDelay(long? dueAt, long now) => dueAt is { } d ? Math.Max(0, d - now) : null;

    private static int Clamp(int n) => Math.Clamp(n, 0, MaxMinutes);
}
