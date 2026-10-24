using WhatDidIDo.Tray.Core.Control;
using WhatDidIDo.Tray.Core.Models;
using WhatDidIDo.Tray.Core.Settings;

namespace WhatDidIDo.Tray.Core.Tests;

public class PopoverBlocksTests
{
    private static readonly MicItem[] Mics = [new("41", "USB headset", "alsa_input.usb")];

    private static SessionStatus Status(string state = "recording", MicStatus? mic = null) =>
        new() { State = state, Monitors = 1, IntervalSeconds = 30, Mic = mic };

    private static MicStatus Mic(bool on, string? state = null, string? label = "USB headset") =>
        new(true, on, on ? "41" : null, null, label, state, false);

    [Fact]
    public void MicTile_States()
    {
        Assert.Equal("No audio in this session", PopoverModel.MicTile(Status(), true, null).Subtitle);
        Assert.False(PopoverModel.MicTile(Status(), true, null).Sensitive);

        var on = PopoverModel.MicTile(Status(mic: Mic(true)), true, Mics);
        Assert.Equal("On · USB headset", on.Subtitle);
        Assert.True(on.On && on.Accent && on.Sensitive);
        Assert.Equal("", on.Glyph);

        Assert.Equal("On · USB headset (waiting for device)", PopoverModel.MicTile(Status(mic: Mic(true, "waiting_for_device")), true, Mics).Subtitle);
        Assert.Equal("On · input", PopoverModel.MicTile(Status(mic: Mic(true, label: null) with { SourceId = "99" }), true, Mics).Subtitle);

        var off = PopoverModel.MicTile(Status(mic: Mic(false)), true, Mics);
        Assert.Equal("Off · no audio", off.Subtitle);
        Assert.False(off.On);
        Assert.Equal("", off.Glyph);

        Assert.False(PopoverModel.MicTile(Status("starting", Mic(false)), true, Mics).Sensitive);
        Assert.True(PopoverModel.MicTile(Status("stopped", Mic(false)), true, Mics).Sensitive);
    }

    [Fact]
    public void PhiTile_OnlyWhenReported_AndNeedsTheAppServer()
    {
        Assert.False(PopoverModel.PhiTile(Status(), true, true).Visible);
        var three = PopoverModel.PhiTile(Status() with { DescribeWithheld = 3 }, true, hasAppServer: false);
        Assert.True(three.Visible);
        Assert.Equal("3 frames withheld", three.Subtitle);
        Assert.False(three.Sensitive);
        Assert.Equal("1 frame withheld", PopoverModel.PhiTile(Status() with { DescribeWithheld = 1 }, true, true).Subtitle);
        Assert.True(PopoverModel.PhiTile(Status() with { DescribeWithheld = 0 }, true, true).Sensitive);
    }

    [Fact]
    public void WindowTile_ReportsOnly()
    {
        var on = PopoverModel.WindowTile(Status() with { WindowActivityAvailable = true }, true);
        Assert.Equal("Recording window titles", on.Subtitle);
        Assert.True(on.On);
        Assert.False(on.Interactive);
        Assert.Equal("Not enabled on the server", PopoverModel.WindowTile(Status(), true).Subtitle);
        Assert.Equal("Off", PopoverModel.WindowTile(null, false).Subtitle);
    }

    [Fact]
    public void Input_Value_ActiveThenSavedThenChoose()
    {
        var sources = new MicView("Microphone", null, [new MicSource("41", "alsa_input.usb", "USB headset", true)]);
        Assert.False(PopoverModel.Input(Status(), true, sources, Mics, "", null).Visible);
        Assert.Equal("USB headset", PopoverModel.Input(Status(mic: Mic(true)), true, sources, Mics, "", null).Value);
        Assert.Equal("Saved one", PopoverModel.Input(Status(mic: Mic(false)), true, sources, Mics, "Saved one", null).Value);
        Assert.Equal("USB headset", PopoverModel.Input(Status(mic: Mic(false)), true, sources, Mics, "", null).Value);
        var none = new MicView("Microphone", null, []);
        var choose = PopoverModel.Input(Status(mic: Mic(false)), true, none, null, "", "input");
        Assert.Equal("Choose…", choose.Value);
        Assert.True(choose.Expanded);
        Assert.Equal("Microphones unavailable", choose.EmptyText);
        Assert.Equal("No microphones found", PopoverModel.Input(Status(mic: Mic(false)), true, none, [], "", null).EmptyText);
    }

    [Fact]
    public void Interval_SessionValue_Drag_AndHold()
    {
        var plain = PopoverModel.Interval(Status(), true, null, null, 0, false);
        Assert.Equal(("30 s", 4, 30, true), (plain.Value, plain.Index, plain.Seconds!.Value, plain.Sensitive));

        // A value set elsewhere that is not a stop shows its own label on the nearest stop.
        var odd = PopoverModel.Interval(Status() with { IntervalSeconds = 40 }, true, null, null, 0, false);
        Assert.Equal(("40 s", 5), (odd.Value, odd.Index));

        var drag = PopoverModel.Interval(Status(), true, 9, null, 0, false);
        Assert.Equal(("5 min", 9, true), (drag.Value, drag.Index, drag.Dragging));

        var hold = new IntervalHold(9, 300, Until: 1000);
        Assert.True(PopoverModel.Interval(Status(), true, null, hold, 999, false).Holding);
        Assert.False(PopoverModel.Interval(Status(), true, null, hold, 1000, false).Holding);
        Assert.False(PopoverModel.Interval(Status() with { IntervalSeconds = 300 }, true, null, hold, 0, false).Holding);

        Assert.False(PopoverModel.Interval(Status("stopped"), true, null, null, 0, false).Sensitive);
        Assert.False(PopoverModel.Interval(Status(), true, null, null, 0, busy: true).Sensitive);
        Assert.False(PopoverModel.Interval(null, false, null, null, 0, false).Visible);
    }

    [Fact]
    public void View_AddsTheMicHintAsANotice()
    {
        var view = PopoverModel.View(Status(mic: Mic(false)), mic: new MicView("Microphone", "Choose a microphone", []));
        Assert.Equal(["Choose a microphone"], view.Notices);
        Assert.True(view.Tiles.Visible);
        Assert.False(PopoverModel.View(null).Tiles.Visible);
    }
}

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wdi-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void MissingFile_IsTheDefaults_AndAChangeIsSaved()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new SettingsStore(path);
        Assert.Equal(new TraySettings(), store.Current);
        store.SaveMicChoice(new PreferredMic("alsa_input.usb", "41", "USB headset"));
        var text = File.ReadAllText(path);
        Assert.Contains("\"microphoneStableId\": \"alsa_input.usb\"", text, StringComparison.Ordinal);
        Assert.Equal(new PreferredMic("alsa_input.usb", "41", "USB headset"), new SettingsStore(path).LoadMicChoice());
    }

    [Fact]
    public void BadValues_FallBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """
            {"pollActive": 0, "pollIdle": 30, "pauseReminderMinutes": 9999, "microphoneLabel": null, // comment
            }
            """);
        var s = new SettingsStore(path).Current;
        Assert.Equal((2, 30, 10, ""), (s.PollActive, s.PollIdle, s.PauseReminderMinutes, s.MicrophoneLabel));
        File.WriteAllText(path, "not json");
        Assert.Equal(new TraySettings(), new SettingsStore(path).Current);
    }
}
