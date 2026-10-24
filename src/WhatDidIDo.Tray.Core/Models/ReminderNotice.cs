namespace WhatDidIDo.Tray.Core.Models;

/// <summary>Which reminder a notification belongs to; its tag, so a new one replaces the last.</summary>
public enum ReminderKind
{
    Pause,
    Mic,
}

/// <summary>What a reminder's button asks for.</summary>
public enum ReminderAction
{
    /// <summary>Pause reminder: resume the session.</summary>
    Resume,
    /// <summary>Microphone reminder: open the flyout with the input list.</summary>
    ChooseMicrophone,
    /// <summary>Either reminder: stop reminding (until the state is left, or speech).</summary>
    StopReminding,
}

/// <summary>A notification button: its label and the arguments it carries back.</summary>
public sealed record ReminderButton(string Label, ReminderAction Action);

/// <summary>An app notification for a reminder (TRAY.md section 5).</summary>
public sealed record ReminderNotice(ReminderKind Kind, string Title, string Body, IReadOnlyList<ReminderButton> Buttons)
{
    /// <summary>All the tray's notifications share this group; the tag is the kind.</summary>
    public const string Group = "whatdidido-reminders";

    public string Tag => TagOf(Kind);

    public static string TagOf(ReminderKind kind) => kind == ReminderKind.Pause ? "pause" : "mic";

    public static ReminderNotice Pause(string body) => new(ReminderKind.Pause, PauseReminder.Title, body,
        [new("Resume", ReminderAction.Resume), new("Stop reminding", ReminderAction.StopReminding)]);

    public static ReminderNotice Mic(string body) => new(ReminderKind.Mic, MicReminder.Title, body,
        [new("Choose microphone", ReminderAction.ChooseMicrophone), new("Stop reminding", ReminderAction.StopReminding)]);

    /// <summary>The arguments a button carries: the reminder and the action.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Arguments(ReminderKind kind, ReminderAction action) =>
        [new("reminder", TagOf(kind)), new("action", action.ToString())];

    /// <summary>A button's or the body's argument string: <c>reminder=pause;action=Resume</c>.</summary>
    public static string Encode(IEnumerable<KeyValuePair<string, string>> arguments) =>
        string.Join(';', arguments.Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>The arguments of a clicked toast; pairs without '=' are ignored, the first of a key wins.</summary>
    public static IReadOnlyDictionary<string, string> Decode(string? text)
    {
        var result = new Dictionary<string, string>();
        foreach (var part in (text ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
                result.TryAdd(part[..eq], part[(eq + 1)..]);
        }
        return result;
    }

    /// <summary>
    /// The reminder and action from a clicked notification's arguments, or null
    /// for anything else (a click on the body, an unknown or stale argument).
    /// </summary>
    public static (ReminderKind Kind, ReminderAction Action)? Parse(IReadOnlyDictionary<string, string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!arguments.TryGetValue("reminder", out var tag) || !arguments.TryGetValue("action", out var name))
            return null;
        ReminderKind? kind = tag switch
        {
            "pause" => ReminderKind.Pause,
            "mic" => ReminderKind.Mic,
            _ => null,
        };
        // Names only: Enum.TryParse would also take "0" or "1,2".
        if (kind is null || !Enum.GetNames<ReminderAction>().Contains(name) || !Enum.TryParse<ReminderAction>(name, out var action))
            return null;
        // Each reminder offers only its own buttons.
        var offered = kind == ReminderKind.Pause
            ? action is ReminderAction.Resume or ReminderAction.StopReminding
            : action is ReminderAction.ChooseMicrophone or ReminderAction.StopReminding;
        return offered ? (kind.Value, action) : null;
    }
}
