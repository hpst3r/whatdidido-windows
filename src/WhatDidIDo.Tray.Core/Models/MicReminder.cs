using WhatDidIDo.Tray.Core.Control;

namespace WhatDidIDo.Tray.Core.Models;

/// <summary>
/// Reminds while the microphone is on and nothing it heard was transcribed to
/// text: a wrong or dead microphone shows up as transcripts that keep arriving
/// but are all empty, so <c>transcription_with_text</c> stops going up.
/// Ported from the extension's lib/mic_reminder.js. Times are milliseconds on
/// a monotonic clock passed in by the caller.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>QuietSince is the first armed observation (<see cref="Armed"/>). It
/// starts over at the poll that sees transcription_with_text go up (speech),
/// go down (the server restarted: the new value is the baseline) or another
/// microphone (stable_id, else source_id); that withdraws a shown reminder and
/// ends "Stop reminding".</item>
/// <item>The first reminder is due at QuietSince + minutes, each later one
/// minutes after the previous reminder (the time it was actually shown).</item>
/// <item>Not armed cancels everything and withdraws.</item>
/// <item>SetMinutes while armed recomputes; already overdue reminds at once;
/// 0 cancels and withdraws, and turning it back on counts from the next poll.</item>
/// <item>Suppress ("Stop reminding") stops reminders until QuietSince starts
/// over or the reminder disarms.</item>
/// </list>
/// </remarks>
public sealed class MicReminder(int minutes = PauseReminder.DefaultMinutes)
{
    public const string Title = "Check your microphone";

    private int _minutes = Math.Clamp(minutes, 0, PauseReminder.MaxMinutes);
    private int? _withText;
    private string? _micId;
    private string? _label;
    private long? _lastReminder;
    private bool _shown;
    private bool _suppressed;

    public int Minutes => _minutes;
    public long? QuietSince { get; private set; }

    /// <summary>When the next reminder is due, or null when none is.</summary>
    public long? DueAt => QuietSince is null || _minutes == 0 || _suppressed
        ? null
        : (_lastReminder ?? QuietSince.Value) + _minutes * PauseReminder.MinuteMs;

    /// <summary>
    /// Whether quiet time counts: recording with the microphone on and a server
    /// that reports transcriptions with text. A microphone waiting for its
    /// device, retrying or pending still counts (a microphone problem too).
    /// Audio limit_reached or failed does not (no microphone choice fixes it),
    /// nor a captured output monitor (its transcripts count too, so speech from
    /// a call would hide a dead microphone).
    /// </summary>
    public static bool Armed(SessionStatus? status, int minutes) =>
        minutes > 0
        && status is { State: "recording", WaitingForUnlock: false, Mic.On: true, AudioOutputMonitor: false, TranscriptionEnabled: true }
        && status.Mic!.State is not ("limit_reached" or "failed")
        && status.TranscriptionWithText is not null;

    public static string Body(string? label, long ms)
    {
        var m = PauseReminder.WholeMinutes(ms);
        var name = string.IsNullOrEmpty(label) ? "the microphone" : label;
        return $"No speech transcribed from {name} for {m} minute{(m == 1 ? "" : "s")}.";
    }

    /// <param name="status">The polled status; null when the session is not running.</param>
    public ReminderEffect Observe(SessionStatus? status, long now)
    {
        if (!Armed(status, _minutes))
        {
            if (QuietSince is null)
                return ReminderEffect.None;
            Clear();
            return ReminderEffect.WithdrawShown;
        }
        var withText = status!.TranscriptionWithText;
        var micId = status.Mic!.StableId ?? status.Mic.SourceId;
        _label = status.Mic.Label;
        if (QuietSince is null || withText != _withText || micId != _micId)
        {
            var shown = _shown;
            QuietSince = now;
            _withText = withText;
            _micId = micId;
            _lastReminder = null;
            _shown = false;
            _suppressed = false;
            return shown ? ReminderEffect.WithdrawShown : ReminderEffect.None;
        }
        // Normally the timer fires first; this catches a missed one.
        return Fire(now);
    }

    public ReminderEffect SetMinutes(int value, long now)
    {
        _minutes = Math.Clamp(value, 0, PauseReminder.MaxMinutes);
        if (QuietSince is null)
            return ReminderEffect.None;
        if (_minutes == 0)
        {
            Clear();
            return ReminderEffect.WithdrawShown;
        }
        return Fire(now);
    }

    public ReminderEffect Fire(long now)
    {
        if (DueAt is not { } due || now < due)
            return ReminderEffect.None;
        _lastReminder = now;
        _shown = true;
        return new ReminderEffect(Body(_label, now - QuietSince!.Value), false);
    }

    public ReminderEffect Suppress()
    {
        if (QuietSince is null)
            return ReminderEffect.None;
        _suppressed = true;
        _shown = false;
        return ReminderEffect.WithdrawShown;
    }

    private void Clear()
    {
        QuietSince = null;
        _withText = null;
        _micId = null;
        _label = null;
        _lastReminder = null;
        _shown = false;
        _suppressed = false;
    }
}
