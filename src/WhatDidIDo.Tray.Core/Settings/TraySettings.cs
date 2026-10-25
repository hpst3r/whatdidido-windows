using System.Text.Json;
using System.Text.Json.Serialization;
using WhatDidIDo.Tray.Core.Models;

namespace WhatDidIDo.Tray.Core.Settings;

/// <summary>
/// The tray's settings (TRAY.md section 6): the extension's keys in camelCase.
/// Never the control token. Start at login is not here: its state is the
/// registry's Run value (<see cref="StartAtLogin"/>).
/// </summary>
public sealed record TraySettings
{
    // Settable, not init-only: the JSON source generator fills init-only
    // properties through an object initializer, so a key missing from the file
    // would come out as 0/false/null instead of keeping the default below.
    public int PollActive { get; set; } = 2;
    public int PollIdle { get; set; } = 10;
    public string MicrophoneStableId { get; set; } = "";
    public string MicrophoneId { get; set; } = "";
    public string MicrophoneLabel { get; set; } = "";
    public bool ShowMicIndicator { get; set; } = true;
    public int PauseReminderMinutes { get; set; } = PauseReminder.DefaultMinutes;
    public int MicReminderMinutes { get; set; } = 10;
    public string AppUrl { get; set; } = "";

    // The extension's GSettings ranges (whatdidido-gnome schemas/*.gschema.xml).
    public const int PollActiveMin = 1, PollActiveMax = 60;
    public const int PollIdleMin = 2, PollIdleMax = 3600;
    public const int ReminderMinutesMax = PauseReminder.MaxMinutes;

    /// <summary>Out-of-range or missing values fall back to their defaults, as GSettings ranges do.</summary>
    public TraySettings Normalized()
    {
        var d = new TraySettings();
        return this with
        {
            PollActive = PollActive is >= PollActiveMin and <= PollActiveMax ? PollActive : d.PollActive,
            PollIdle = PollIdle is >= PollIdleMin and <= PollIdleMax ? PollIdle : d.PollIdle,
            MicrophoneStableId = MicrophoneStableId ?? "",
            MicrophoneId = MicrophoneId ?? "",
            MicrophoneLabel = MicrophoneLabel ?? "",
            PauseReminderMinutes = PauseReminderMinutes is >= 0 and <= PauseReminder.MaxMinutes ? PauseReminderMinutes : d.PauseReminderMinutes,
            MicReminderMinutes = MicReminderMinutes is >= 0 and <= PauseReminder.MaxMinutes ? MicReminderMinutes : d.MicReminderMinutes,
            AppUrl = AppUrl ?? "",
        };
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(TraySettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// <c>%LOCALAPPDATA%\whatdidido-windows\settings.json</c>, read once and
/// rewritten atomically (temp file, then replace) on every change. A missing
/// or unreadable file is the defaults; it is never fatal.
/// </summary>
public sealed class SettingsStore : IMicChoiceStore
{
    private readonly string? _path;

    public SettingsStore(string? path)
    {
        _path = path;
        Current = Read(path);
    }

    public TraySettings Current { get; private set; }

    /// <summary>Raised after a change was applied (saved or not).</summary>
    public event EventHandler? Changed;

    public static string? DefaultPath()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        return string.IsNullOrEmpty(local) || !Path.IsPathFullyQualified(local)
            ? null
            : Path.Combine(local, "whatdidido-windows", "settings.json");
    }

    public void Update(Func<TraySettings, TraySettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var next = change(Current).Normalized();
        if (next == Current)
            return;
        Current = next;
        Write();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public PreferredMic LoadMicChoice() =>
        new(Current.MicrophoneStableId, Current.MicrophoneId, Current.MicrophoneLabel);

    public void SaveMicChoice(PreferredMic choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        Update(s => s with { MicrophoneStableId = choice.StableId, MicrophoneId = choice.Id, MicrophoneLabel = choice.Label });
    }

    private static TraySettings Read(string? path)
    {
        if (path is null)
            return new TraySettings();
        try
        {
            using var stream = File.OpenRead(path);
            return (JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.TraySettings) ?? new TraySettings()).Normalized();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new TraySettings();
        }
    }

    private void Write()
    {
        if (_path is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(Current, SettingsJsonContext.Default.TraySettings));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The setting still applies for this run.
        }
    }
}
