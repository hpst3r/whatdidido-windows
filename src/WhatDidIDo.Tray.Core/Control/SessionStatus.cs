using System.Text.Json;

namespace WhatDidIDo.Tray.Core.Control;

/// <summary>The <c>mic</c> object of <c>GET /control/v1/status</c> (CONTROL_API.md, amendment 2).</summary>
public sealed record MicStatus(
    bool Available,
    bool On,
    string? SourceId,
    string? StableId,
    string? Label,
    string? State,
    bool Remembered);

/// <summary>
/// <c>GET /control/v1/status</c>, read tolerantly as the extension does: a key
/// that is missing or of the wrong type is null (counts, strings) or false
/// (flags), never an error. Keys a client does not know are additive and ignored.
/// </summary>
public sealed record SessionStatus
{
    public string? State { get; init; }
    public int? Samples { get; init; }
    public int? Pending { get; init; }
    public int? Monitors { get; init; }
    public int? IntervalSeconds { get; init; }
    public string? LastError { get; init; }
    public string? Reason { get; init; }
    public bool WaitingForUnlock { get; init; }
    public MicStatus? Mic { get; init; }
    /// <summary>A number only when the server reports it (PHI guard).</summary>
    public double? DescribeWithheld { get; init; }
    /// <summary>Null without <c>--window-activity</c>.</summary>
    public bool? WindowActivityAvailable { get; init; }
    public long? WindowActivityEpoch { get; init; }
    public bool TranscriptionEnabled { get; init; }
    public int? TranscriptionWithText { get; init; }
    public bool AudioOutputMonitor { get; init; }
    public string? Autostart { get; init; }

    public static SessionStatus Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new ControlException("invalid JSON response");
        var mic = Obj(root, "mic");
        var win = Obj(root, "window_activity");
        return new SessionStatus
        {
            State = Str(root, "state"),
            Samples = Count(root, "samples"),
            Pending = Count(root, "pending"),
            Monitors = Count(root, "monitors"),
            IntervalSeconds = Count(root, "interval_seconds"),
            LastError = Str(root, "last_error"),
            Reason = Str(root, "reason"),
            WaitingForUnlock = Flag(root, "waiting_for_unlock"),
            Mic = mic is { } m
                ? new MicStatus(Flag(m, "available"), Flag(m, "on"), Str(m, "source_id"), Str(m, "stable_id"),
                    Str(m, "label"), Str(m, "state"), Flag(m, "remembered"))
                : null,
            DescribeWithheld = root.TryGetProperty("describe_withheld", out var d) && d.ValueKind == JsonValueKind.Number
                ? d.GetDouble() : null,
            WindowActivityAvailable = win is { } w ? Flag(w, "available") : null,
            WindowActivityEpoch = win is { } w2 && w2.TryGetProperty("epoch", out var e)
                && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var epoch) && epoch >= 0 ? epoch : null,
            TranscriptionEnabled = Flag(root, "transcription_enabled"),
            TranscriptionWithText = Count(root, "transcription_with_text"),
            AudioOutputMonitor = Flag(root, "audio_output_monitor"),
            Autostart = Str(root, "autostart"),
        };
    }

    private static JsonElement? Obj(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Flag(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;

    // The extension's isCount: a non-negative integer, else null.
    private static int? Count(JsonElement o, string key) =>
        o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n) && n >= 0 ? n : null;
}
