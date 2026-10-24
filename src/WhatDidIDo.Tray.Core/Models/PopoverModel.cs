using System.Globalization;
using WhatDidIDo.Tray.Core.Control;

namespace WhatDidIDo.Tray.Core.Models;

/// <summary>The status card's leading mark (POPOVER.md 4.2).</summary>
public enum StatusMark
{
    Idle,
    Recording,
    Paused,
    Power,
    Info,
}

public enum StatusKind
{
    Session,
    NotRunning,
    Error,
}

/// <summary>The round primary button: which command, and whether it is accent-filled (Resume).</summary>
public sealed record PrimaryButton(string Command, string AccessibleName, bool Accent, bool Sensitive);

public sealed record StatusCard(
    StatusKind Kind,
    StatusMark Mark,
    bool Pulse,
    bool Paused,
    string Title,
    string Subtitle,
    PrimaryButton? Primary);

/// <summary>The footer; while confirming, Stop session turns into Cancel and Stop now.</summary>
public sealed record Footer(bool Visible, string Text, bool StopVisible, bool Confirm, bool StopSensitive);

/// <summary>Widget state the flyout owns and passes back in (POPOVER.md 6, <c>ui</c>).</summary>
public sealed record PopoverUi(bool CommandBusy = false, bool StopConfirm = false);

/// <summary>Everything the flyout renders for the blocks of slice <c>tray-core</c>.</summary>
public sealed record PopoverView(
    bool Running,
    string? State,
    StatusCard Status,
    IReadOnlyList<string> Notices,
    Footer Footer);

/// <summary>
/// The flyout as plain data, ported from the extension's lib/popover_model.js
/// (same strings and rules). The flyout renders the returned object and owns
/// only widget state.
/// </summary>
public static class PopoverModel
{
    public const string NotRunningTitle = "Capture not running";
    public const string NotRunningDetail = "No work session is running on this computer. Start one to record; "
        + "everything already captured stays browsable.";
    public const string PollErrorTitle = "Can't read the capture status";
    public const string PausedText = "Nothing is captured while paused";

    /// <summary>The app's interval stops (seconds), 5 s .. 1 h.</summary>
    public static readonly IReadOnlyList<int> IntervalSteps = [5, 10, 15, 20, 30, 45, 60, 90, 120, 300, 600, 900, 1800, 3600];

    /// <summary>
    /// The app's interval label (core/time.ts): "5 s", "2 min", "1 h", "1.5 h".
    /// Minutes are rounded, so 90 s is "2 min", as in the app.
    /// </summary>
    public static string IntervalLabel(int? seconds)
    {
        if (seconds is not { } s || s <= 0)
            return "?";
        if (s < 60)
            return $"{s} s";
        if (s < 3600)
            return $"{Math.Round(s / 60.0, MidpointRounding.AwayFromZero)} min";
        var hours = Math.Round(s / 360.0, MidpointRounding.AwayFromZero) / 10;
        return $"{hours.ToString("0.#", CultureInfo.InvariantCulture)} h";
    }

    /// <summary>Index in IntervalSteps of the stop nearest to seconds (ties: the smaller stop; null: 0).</summary>
    public static int NearestStep(int? seconds)
    {
        if (seconds is not { } s)
            return 0;
        var best = 0;
        for (var i = 1; i < IntervalSteps.Count; i++)
        {
            if (Math.Abs(IntervalSteps[i] - s) < Math.Abs(IntervalSteps[best] - s))
                best = i;
        }
        return best;
    }

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")}";

    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    public static StatusCard StatusBlock(SessionStatus? status, bool running, string? pollError, long? pausedMs, bool busy)
    {
        if (!running || status is null)
        {
            var error = !string.IsNullOrEmpty(pollError);
            return new StatusCard(
                error ? StatusKind.Error : StatusKind.NotRunning,
                error ? StatusMark.Info : StatusMark.Power,
                Pulse: false,
                Paused: false,
                error ? PollErrorTitle : NotRunningTitle,
                error ? pollError! : NotRunningDetail,
                Primary: null);
        }

        var state = status.State;
        string subtitle;
        if (state == "paused")
            subtitle = pausedMs is { } ms ? $"{PauseReminder.PausedForText(ms)} · nothing is captured" : PausedText;
        else if (state == "stopped")
            subtitle = "Start recording when you're ready";
        else if (state == "failed")
            subtitle = "The session stopped after an error";
        else
        {
            var mic = status.Mic is { Available: true } m ? $" · mic {(m.On ? "on" : "off")}" : "";
            subtitle = $"{Plural(status.Monitors ?? 0, "monitor")} · every {IntervalLabel(status.IntervalSeconds)}{mic}";
        }

        var allowed = StatusModel.Allowed(state);
        PrimaryButton? primary =
            allowed.Pause ? new("pause", "Pause recording", false, !busy)
            : allowed.Resume ? new("resume", "Resume recording", true, !busy)
            : allowed.Start ? new("start", "Start recording", false, !busy)
            : null;

        return new StatusCard(
            StatusKind.Session,
            state == "recording" ? StatusMark.Recording : state == "paused" ? StatusMark.Paused : StatusMark.Idle,
            Pulse: state == "recording",
            Paused: state == "paused",
            state == "recovering" && status.WaitingForUnlock ? "Waiting for unlock" : StatusModel.StateLabel(state),
            subtitle,
            primary);
    }

    public static Footer FooterBlock(SessionStatus? status, bool running, PopoverUi ui)
    {
        if (!running || status is null)
            return new Footer(false, "", false, false, false);
        var canStop = StatusModel.Allowed(status.State).Stop;
        var confirm = canStop && ui.StopConfirm;
        var text = status.State == "paused"
            ? PausedText
            : $"{(status.Samples ?? 0).ToString("N0", EnUs)} samples · {(status.Pending ?? 0).ToString("N0", EnUs)} pending";
        return new Footer(true, text, canStop && !confirm, confirm, !ui.CommandBusy);
    }

    /// <param name="status">GET /control/v1/status, or null.</param>
    /// <param name="running">False: not running (no control file, refused).</param>
    /// <param name="pollError">ErrorText of a poll failure other than "not running".</param>
    /// <param name="notice">The last failed action's text.</param>
    /// <param name="pausedMs">PauseReminder.PausedFor(), or null.</param>
    public static PopoverView View(
        SessionStatus? status,
        bool running = true,
        string? pollError = null,
        string? notice = null,
        long? pausedMs = null,
        PopoverUi? ui = null)
    {
        ui ??= new PopoverUi();
        running = running && status is not null;
        var notices = new List<string>();
        if (!string.IsNullOrEmpty(notice))
            notices.Add(notice);
        if (running && StatusModel.LastErrorText(status) is { } lastError)
            notices.Add(lastError);
        return new PopoverView(
            running,
            running ? status!.State : null,
            StatusBlock(status, running, pollError, pausedMs, ui.CommandBusy),
            notices,
            FooterBlock(status, running, ui));
    }
}
