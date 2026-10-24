using WhatDidIDo.Tray.Core.Control;

namespace WhatDidIDo.Tray.Core.Models;

/// <summary>The saved choice (settings <c>microphoneStableId</c>, <c>microphoneId</c>, <c>microphoneLabel</c>).</summary>
public sealed record PreferredMic(string StableId = "", string Id = "", string Label = "")
{
    public bool HasChoice => StableId.Length > 0 || Id.Length > 0;
}

/// <summary>An entry of the input picker; <see cref="Checked"/> is the saved choice, if connected.</summary>
public sealed record MicSource(string Id, string StableId, string Label, bool Checked);

/// <summary>What the flyout shows about the microphone (the extension's <c>MicController.view()</c>).</summary>
public sealed record MicView(string SwitchLabel, string? Hint, IReadOnlyList<MicSource> Sources);

/// <summary>
/// Explicit microphone selection, ported from the extension's lib/mic_model.js
/// (whatdidido RELIABILITY.md section 10, issue #1): the tray never picks a
/// microphone the user did not choose.
/// </summary>
public static class MicModel
{
    public const string ChooseText = "Choose a microphone";

    private static string? StateText(string? state) => state switch
    {
        "waiting_for_device" => "waiting for device",
        "retrying" => "restarting",
        "limit_reached" => "limit reached",
        "failed" => "failed",
        _ => null,
    };

    /// <summary>
    /// The microphone matching the saved stable id; else (only when no stable
    /// id is saved, or the server reports none) the one matching the legacy id;
    /// else null. Never any other microphone. Several items with the saved
    /// stable id are ambiguous: null. A legacy id must also keep its saved
    /// label, since serials restart and may then name another device.
    /// </summary>
    public static MicItem? Choose(IReadOnlyList<MicItem>? mics, PreferredMic preferred)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        if (mics is null)
            return null;
        if (preferred.StableId.Length > 0 && mics.Any(m => m.ReportsStableId))
        {
            var same = mics.Where(m => m.StableId == preferred.StableId).ToList();
            return same.Count == 1 ? same[0] : null;
        }
        if (preferred.Id.Length == 0)
            return null;
        return mics.FirstOrDefault(m => m.Id == preferred.Id
            && (preferred.Label.Length == 0 || m.Label.Length == 0 || m.Label == preferred.Label));
    }

    /// <summary>
    /// Migration on an inventory read: only the legacy id and its label are
    /// saved and that microphone is present with a stable id: the stable id to
    /// store, else null. A half-cleared choice never adopts a stable id again.
    /// </summary>
    public static string? MigrateStableId(IReadOnlyList<MicItem>? mics, PreferredMic preferred)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        if (preferred.StableId.Length > 0 || preferred.Id.Length == 0 || preferred.Label.Length == 0)
            return null;
        return Choose(mics, preferred)?.StableId;
    }

    public static IReadOnlyList<MicSource> Sources(IReadOnlyList<MicItem>? mics, PreferredMic preferred)
    {
        var chosen = Choose(mics, preferred);
        return (mics ?? []).Select(m => new MicSource(m.Id, m.StableId ?? "", m.Label.Length > 0 ? m.Label : m.Id,
            ReferenceEquals(m, chosen))).ToList();
    }

    /// <summary>Body for <c>POST /control/v1/mic</c>, or null when the saved choice is not in the inventory.</summary>
    public static Dictionary<string, object>? Request(bool on, IReadOnlyList<MicItem>? mics, PreferredMic preferred, string inventory)
    {
        if (!on)
            return new() { ["on"] = false };
        return Choose(mics, preferred) is { } mic
            ? new() { ["on"] = true, ["source_id"] = mic.Id, ["inventory"] = inventory }
            : null;
    }

    public static string MissingText(PreferredMic preferred)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        return $"{(preferred.Label.Length > 0 ? preferred.Label : "The chosen microphone")} is not connected";
    }

    /// <summary>The microphone the session actually uses: mic.label, else the inventory item with mic.source_id.</summary>
    public static string? ActiveLabel(MicStatus? mic, IReadOnlyList<MicItem>? mics)
    {
        if (mic?.Label is { Length: > 0 } label)
            return label;
        if (mic?.SourceId is not { Length: > 0 } source || mics is null)
            return null;
        return mics.FirstOrDefault(m => m.Id == source) is { } m ? (m.Label.Length > 0 ? m.Label : m.Id) : null;
    }

    /// <summary>"Microphone", or the active one and its trouble: "Microphone: USB headset (waiting for device)".</summary>
    public static string SwitchLabel(SessionStatus? status, bool running, IReadOnlyList<MicItem>? mics)
    {
        var mic = running ? status?.Mic : null;
        if (mic is null)
            return "Microphone";
        var label = mic.On ? ActiveLabel(mic, mics) : null;
        var text = label is null ? "Microphone" : $"Microphone: {label}";
        return StateText(mic.State) is { } state ? $"{text} ({state})" : text;
    }

    /// <summary>
    /// Why the microphone can't be turned on, while it is off. prompt: the user
    /// turned it on without a saved choice; missing: the client refused the
    /// saved choice as not connected.
    /// </summary>
    public static string? Hint(SessionStatus? status, bool running, IReadOnlyList<MicItem>? mics, PreferredMic preferred,
        bool prompt = false, bool missing = false)
    {
        ArgumentNullException.ThrowIfNull(preferred);
        var mic = running ? status?.Mic : null;
        if (mic is null || !mic.Available || mic.On)
            return null;
        if (!preferred.HasChoice)
            return prompt ? ChooseText : null;
        if (missing || (mics is not null && Choose(mics, preferred) is null))
            return MissingText(preferred);
        return null;
    }

    /// <summary>Choosing a source while the session's microphone is on switches it over, unless it is the same device.</summary>
    public static bool LiveSwitchNeeded(MicStatus? live, bool running, MicSource item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!running || live is not { On: true })
            return false;
        if (live.StableId is { Length: > 0 } stable && item.StableId.Length > 0)
            return stable != item.StableId;
        return live.SourceId != item.Id;
    }
}
