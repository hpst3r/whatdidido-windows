using WhatDidIDo.Tray.Core.Control;

namespace WhatDidIDo.Tray.Core.Models;

/// <summary>What a state allows, mirroring the server's own guards (work_session_backend).</summary>
public sealed record AllowedActions(bool Start, bool Pause, bool Resume, bool Stop);

/// <summary>The tray icon's shape (TRAY.md section 3.1); every state has its own.</summary>
public enum TrayIconKind
{
    Offline,
    Stopped,
    Busy,
    Recording,
    Paused,
    Recovering,
    Failed,
}

/// <summary>The notification-area icon: shape, the microphone badge and the tooltip.</summary>
public sealed record TrayView(TrayIconKind Kind, bool MicBadge, string Tooltip, string Label);

/// <summary>
/// Pure decisions about the session's status, ported from the extension's
/// lib/model.js (same names, same strings).
/// </summary>
public static class StatusModel
{
    public const string NotRunningText = "whatdidido not running";

    public static readonly IReadOnlyList<string> States =
        ["stopped", "starting", "recording", "paused", "recovering", "stopping", "failed"];

    public static AllowedActions Allowed(string? state) => state switch
    {
        "stopped" or "failed" => new(Start: true, Pause: false, Resume: false, Stop: false),
        "starting" or "recovering" => new(false, false, false, Stop: true),
        "recording" => new(false, Pause: true, Resume: false, Stop: true),
        "paused" => new(false, false, Resume: true, Stop: true),
        _ => new(false, false, false, false),
    };

    public static string StateLabel(string? state) => state switch
    {
        "stopped" => "Stopped",
        "starting" => "Starting…",
        "recording" => "Recording",
        "paused" => "Paused",
        "recovering" => "Reconnecting…",
        "stopping" => "Stopping…",
        "failed" => "Failed",
        _ => "Unknown state",
    };

    public static TrayIconKind IconKind(string? state, bool running) => !running
        ? TrayIconKind.Offline
        : state switch
        {
            "stopped" => TrayIconKind.Stopped,
            "starting" or "stopping" => TrayIconKind.Busy,
            "recording" => TrayIconKind.Recording,
            "paused" => TrayIconKind.Paused,
            "recovering" => TrayIconKind.Recovering,
            _ => TrayIconKind.Failed,
        };

    // mic.state in which nothing is or will be captured although mic.on is
    // true (RELIABILITY.md section 4): no live-microphone cue.
    public static bool MicLive(SessionStatus? status, bool running) =>
        running && status?.Mic is { On: true } mic && mic.State is not ("limit_reached" or "failed");

    private static readonly string[] BusyStates = ["starting", "recording", "recovering", "stopping"];

    /// <summary>Seconds until the next status poll.</summary>
    public static int PollPeriod(string? state, bool flyoutOpen, int fast = 2, int slow = 10) =>
        Math.Max(1, flyoutOpen || (state is not null && BusyStates.Contains(state)) ? fast : slow);

    public static TrayView Tray(SessionStatus? status, bool running = true, bool showMic = true)
    {
        running = running && status is not null;
        var state = running ? status!.State : null;
        var label = running ? StateLabel(state) : NotRunningText;
        var live = MicLive(status, running);
        return new TrayView(IconKind(state, running), live && showMic,
            $"whatdidido: {label}{(live ? ", microphone on" : "")}", label);
    }

    public static string? LastErrorText(SessionStatus? status)
    {
        if (status is null)
            return null;
        if (!string.IsNullOrEmpty(status.LastError))
            return $"Last error: {status.LastError}{(string.IsNullOrEmpty(status.Reason) ? "" : $" ({status.Reason})")}";
        if (status.State == "failed" && !string.IsNullOrEmpty(status.Reason))
            return $"Failed: {status.Reason}";
        return null;
    }

    private static readonly Dictionary<string, string> ErrorCodes = new()
    {
        ["not_allowed"] = "Not allowed in the current state",
        ["inventory_changed"] = "Microphones changed; try again",
        ["unavailable"] = "Microphone capture is not enabled on the server",
        ["forbidden"] = "Control token rejected",
        ["failed"] = "The command failed",
        ["invalid"] = "Request rejected as invalid",
        ["microphone_unset"] = "Choose a microphone",
    };

    public static string ErrorText(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is ControlException c)
        {
            if (c.NotRunning)
                return NotRunningText;
            if (c.Code == "microphone_missing" && !string.IsNullOrEmpty(c.Message))
                return c.Message;
            if (c.Code is not null && ErrorCodes.TryGetValue(c.Code, out var text))
                return text;
            if (c.Status != 0)
                return $"HTTP {c.Status}";
        }
        return error.Message;
    }
}
