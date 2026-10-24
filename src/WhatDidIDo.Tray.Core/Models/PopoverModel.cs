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

/// <summary>
/// A Quick Settings tile. <see cref="Interactive"/>: clicking does something
/// (the mic tile toggles, the PHI tile opens a link); the Active window tile
/// only reports (TRAY.md T8). <see cref="Sensitive"/> false dims it.
/// </summary>
public sealed record Tile(
    bool Visible,
    string Title,
    string Subtitle,
    string Glyph,
    bool On,
    bool Accent,
    bool Sensitive,
    bool Interactive,
    string AccessibleName,
    double? Count = null);

public sealed record Tiles(bool Visible, Tile Mic, Tile Phi, Tile Window);

/// <summary>An entry of the input picker.</summary>
public sealed record InputItem(int Index, string Label, bool Checked, MicSource Source);

/// <summary>The input picker: a row with the active microphone, expanding to the sources.</summary>
public sealed record InputBlock(
    bool Visible,
    string Value,
    bool Expanded,
    bool Sensitive,
    string AccessibleName,
    IReadOnlyList<InputItem> Items,
    string? EmptyText);

/// <summary>
/// The screenshot interval slider. <see cref="Index"/> is the stop shown
/// (dragged, held after a commit, or the nearest to the session's value);
/// <see cref="Value"/> its label, or the session's own value when that is not a stop.
/// </summary>
public sealed record IntervalBlock(
    bool Visible,
    string Value,
    int Index,
    int Max,
    int? Seconds,
    int? SessionSeconds,
    bool Dragging,
    bool Holding,
    bool Sensitive);

/// <summary>A committed stop kept on the slider until a status reports it or <see cref="Until"/> passes.</summary>
public sealed record IntervalHold(int Index, int Seconds, long Until);

/// <summary>Widget state the flyout owns and passes back in (POPOVER.md 6, <c>ui</c>).</summary>
/// <param name="Expanded">"input" while the input picker is open, else null.</param>
/// <param name="DragIndex">The stop being dragged or committed, else null.</param>
/// <param name="Hold">After a commit, else null (<see cref="PopoverModel.HeldIndex"/>).</param>
public sealed record PopoverUi(
    bool CommandBusy = false,
    bool StopConfirm = false,
    string? Expanded = null,
    int? DragIndex = null,
    IntervalHold? Hold = null,
    bool IntervalBusy = false);

/// <summary>Everything the flyout renders (slices <c>tray-core</c> and <c>tray-popover</c>).</summary>
public sealed record PopoverView(
    bool Running,
    string? State,
    StatusCard Status,
    IReadOnlyList<string> Notices,
    Tiles Tiles,
    InputBlock Input,
    IntervalBlock Interval,
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

    /// <summary>How long a committed stop is held while no status reports it (POPOVER.md Amendment 2.3).</summary>
    public const long IntervalHoldMs = 3000;

    // The states in which the interval can change.
    private static readonly string[] LiveStates = ["recording", "paused", "starting", "recovering"];

    // The states in which the microphone can be switched (model.js MIC_STATES);
    // starting and stopping are transient, so they are waited out.
    private static readonly string[] MicStates = ["stopped", "failed", "recording", "paused", "recovering"];

    /// <summary>
    /// The stop the slider keeps after a commit: the held index until a status
    /// reports its seconds or its deadline passes, else null.
    /// </summary>
    public static int? HeldIndex(IntervalHold? hold, int? sessionSeconds, long nowMs)
    {
        if (hold is null || hold.Index < 0 || hold.Index >= IntervalSteps.Count)
            return null;
        if (sessionSeconds == hold.Seconds || nowMs >= hold.Until)
            return null;
        return hold.Index;
    }

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

    public static Tile MicTile(SessionStatus? status, bool running, IReadOnlyList<MicItem>? mics)
    {
        var mic = running ? status?.Mic : null;
        string subtitle;
        if (mic is not { Available: true })
            subtitle = "No audio in this session";
        else if (mic.On)
        {
            // SwitchLabel is "Microphone[: <label>][ (<trouble>)]"; reuse its trouble text.
            var label = MicModel.ActiveLabel(mic, mics);
            var full = MicModel.SwitchLabel(status, running, mics);
            var head = label is null ? "Microphone" : $"Microphone: {label}";
            var trouble = full.StartsWith(head, StringComparison.Ordinal) ? full[head.Length..] : "";
            subtitle = $"On · {label ?? "input"}{trouble}";
        }
        else
            subtitle = "Off · no audio";
        var on = mic is { Available: true, On: true };
        var sensitive = mic is { Available: true } && status!.State is { } state && MicStates.Contains(state);
        return new Tile(true, "Microphone", subtitle, on ? "" : "", on, on, sensitive, Interactive: true, "Microphone");
    }

    /// <param name="hasAppServer">The tile opens the app's Timeline, so it needs the app server (slice tray-app).</param>
    public static Tile PhiTile(SessionStatus? status, bool running, bool hasAppServer)
    {
        var n = running ? status?.DescribeWithheld : null;
        var visible = n is { } v && double.IsFinite(v);
        var subtitle = visible
            ? $"{n!.Value.ToString("0.##", CultureInfo.InvariantCulture)} frame{(n == 1 ? "" : "s")} withheld"
            : "";
        return new Tile(visible, "PHI guard", subtitle, "", On: false, Accent: false, visible && hasAppServer,
            Interactive: true, "PHI guard: review withheld frames in Timeline", visible ? n : null);
    }

    /// <summary>
    /// Windows reads the focused window in the session itself (TRAY.md T8):
    /// the tile reports whether the session records windows and has no switch.
    /// </summary>
    public static Tile WindowTile(SessionStatus? status, bool running)
    {
        var on = running && status?.WindowActivityAvailable == true;
        var subtitle = on ? "Recording window titles" : running ? "Not enabled on the server" : "Off";
        return new Tile(true, "Active window", subtitle, "", on, on, on, Interactive: false, $"Active window: {subtitle}");
    }

    public static InputBlock Input(SessionStatus? status, bool running, MicView? micView, IReadOnlyList<MicItem>? mics,
        string micChoiceLabel, string? expanded)
    {
        ArgumentNullException.ThrowIfNull(micChoiceLabel);
        var mic = running ? status?.Mic : null;
        var visible = mic is { Available: true };
        var sources = micView?.Sources ?? [];
        var active = mic is { On: true } ? MicModel.ActiveLabel(mic, mics) : null;
        var saved = micChoiceLabel.Length > 0 ? micChoiceLabel : sources.FirstOrDefault(x => x.Checked)?.Label ?? "";
        var value = active ?? (saved.Length > 0 ? saved : "Choose…");
        return new InputBlock(
            visible,
            value,
            visible && expanded == "input",
            visible,
            $"Microphone input: {value}",
            sources.Select((item, i) => new InputItem(i, item.Label, item.Checked, item)).ToList(),
            sources.Count > 0 ? null : mics is not null ? "No microphones found" : "Microphones unavailable");
    }

    public static IntervalBlock Interval(SessionStatus? status, bool running, int? dragIndex, IntervalHold? hold, long nowMs, bool busy)
    {
        var max = IntervalSteps.Count - 1;
        var dragging = dragIndex is { } d && d >= 0 && d <= max;
        var seconds = running ? status?.IntervalSeconds : null;
        var held = dragging || !running ? null : HeldIndex(hold, seconds, nowMs);
        var shown = dragging ? dragIndex : held;
        return new IntervalBlock(
            running,
            shown is { } i ? IntervalLabel(IntervalSteps[i]) : IntervalLabel(seconds),
            shown ?? NearestStep(seconds),
            max,
            shown is { } j ? IntervalSteps[j] : seconds,
            seconds,
            dragging,
            held is not null,
            running && status?.State is { } state && LiveStates.Contains(state) && !busy);
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
    /// <param name="mic">MicController.View(), or null.</param>
    /// <param name="mics">MicController.Mics (null: unknown).</param>
    /// <param name="micChoiceLabel">The saved choice's label.</param>
    /// <param name="hasAppServer">Whether an app server was found (slice tray-app).</param>
    /// <param name="nowMs">The clock <see cref="IntervalHold.Until"/> is on.</param>
    public static PopoverView View(
        SessionStatus? status,
        bool running = true,
        string? pollError = null,
        string? notice = null,
        long? pausedMs = null,
        PopoverUi? ui = null,
        MicView? mic = null,
        IReadOnlyList<MicItem>? mics = null,
        string micChoiceLabel = "",
        bool hasAppServer = false,
        long nowMs = 0)
    {
        ui ??= new PopoverUi();
        running = running && status is not null;
        var notices = new List<string>();
        if (!string.IsNullOrEmpty(notice))
            notices.Add(notice);
        if (running && StatusModel.LastErrorText(status) is { } lastError)
            notices.Add(lastError);
        if (mic?.Hint is { } hint)
            notices.Add(hint);
        return new PopoverView(
            running,
            running ? status!.State : null,
            StatusBlock(status, running, pollError, pausedMs, ui.CommandBusy),
            notices,
            new Tiles(running, MicTile(status, running, mics), PhiTile(status, running, hasAppServer), WindowTile(status, running)),
            Input(status, running, mic, mics, micChoiceLabel, ui.Expanded),
            Interval(status, running, ui.DragIndex, ui.Hold, nowMs, ui.IntervalBusy),
            FooterBlock(status, running, ui));
    }
}
