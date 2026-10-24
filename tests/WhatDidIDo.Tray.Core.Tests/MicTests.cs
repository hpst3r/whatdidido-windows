using WhatDidIDo.Tray.Core.Control;
using WhatDidIDo.Tray.Core.Models;

namespace WhatDidIDo.Tray.Core.Tests;

public class MicModelTests
{
    private static readonly MicItem Usb = new("41", "USB headset", "alsa_input.usb");
    private static readonly MicItem Laptop = new("42", "Laptop mic", "alsa_input.pci");
    private static readonly MicItem[] Both = [Usb, Laptop];

    [Fact]
    public void Choose_ByStableId_EvenWithANewSerial()
    {
        var renumbered = Usb with { Id = "77" };
        Assert.Same(renumbered, MicModel.Choose([renumbered, Laptop], new PreferredMic("alsa_input.usb", "41", "USB headset")));
    }

    [Fact]
    public void Choose_NeverFallsBackToAnotherMicrophone()
    {
        Assert.Null(MicModel.Choose([Laptop], new PreferredMic("alsa_input.usb", "41", "USB headset")));
        Assert.Null(MicModel.Choose(Both, new PreferredMic()));
        Assert.Null(MicModel.Choose(null, new PreferredMic("alsa_input.usb")));
    }

    [Fact]
    public void Choose_TwoWithTheSameStableId_IsAmbiguous() =>
        Assert.Null(MicModel.Choose([Usb, Usb with { Id = "43" }], new PreferredMic("alsa_input.usb")));

    [Fact]
    public void Choose_LegacyId_OnlyWithoutStableIds_AndWithItsLabel()
    {
        MicItem[] old = [new("41", "USB headset", null, ReportsStableId: false)];
        Assert.Same(old[0], MicModel.Choose(old, new PreferredMic("alsa_input.usb", "41", "USB headset")));
        Assert.Null(MicModel.Choose(old, new PreferredMic("", "41", "Other device")));
        // The server reports stable ids, and the saved one is absent: no legacy match.
        Assert.Null(MicModel.Choose([Usb with { StableId = "other" }], new PreferredMic("alsa_input.usb", "41", "USB headset")));
    }

    [Fact]
    public void MigrateStableId()
    {
        Assert.Equal("alsa_input.usb", MicModel.MigrateStableId(Both, new PreferredMic("", "41", "USB headset")));
        Assert.Null(MicModel.MigrateStableId(Both, new PreferredMic("x", "41", "USB headset")));
        Assert.Null(MicModel.MigrateStableId(Both, new PreferredMic("", "41", "")));
    }

    [Fact]
    public void Request()
    {
        Assert.Equal(new Dictionary<string, object> { ["on"] = false }, MicModel.Request(false, null, new PreferredMic(), ""));
        Assert.Equal(new Dictionary<string, object> { ["on"] = true, ["source_id"] = "41", ["inventory"] = "inv" },
            MicModel.Request(true, Both, new PreferredMic("alsa_input.usb"), "inv"));
        Assert.Null(MicModel.Request(true, [Laptop], new PreferredMic("alsa_input.usb"), "inv"));
    }

    [Fact]
    public void Texts()
    {
        Assert.Equal("USB headset is not connected", MicModel.MissingText(new PreferredMic("x", "", "USB headset")));
        Assert.Equal("The chosen microphone is not connected", MicModel.MissingText(new PreferredMic("x")));
        var status = new SessionStatus { State = "recording", Mic = new MicStatus(true, true, "41", null, null, "waiting_for_device", false) };
        Assert.Equal("Microphone: USB headset (waiting for device)", MicModel.SwitchLabel(status, true, Both));
        Assert.Equal("Microphone", MicModel.SwitchLabel(status, false, Both));
    }

    [Fact]
    public void Hint()
    {
        var off = new SessionStatus { State = "recording", Mic = new MicStatus(true, false, null, null, null, null, false) };
        Assert.Null(MicModel.Hint(off, true, Both, new PreferredMic()));
        Assert.Equal("Choose a microphone", MicModel.Hint(off, true, Both, new PreferredMic(), prompt: true));
        Assert.Equal("USB headset is not connected", MicModel.Hint(off, true, [Laptop], new PreferredMic("alsa_input.usb", "", "USB headset")));
        Assert.Null(MicModel.Hint(off with { Mic = off.Mic! with { On = true } }, true, [Laptop], new PreferredMic("alsa_input.usb")));
    }

    [Fact]
    public void LiveSwitchNeeded()
    {
        var item = new MicSource("42", "alsa_input.pci", "Laptop mic", false);
        Assert.True(MicModel.LiveSwitchNeeded(new MicStatus(true, true, "41", "alsa_input.usb", null, null, false), true, item));
        Assert.False(MicModel.LiveSwitchNeeded(new MicStatus(true, true, "99", "alsa_input.pci", null, null, false), true, item));
        Assert.False(MicModel.LiveSwitchNeeded(new MicStatus(true, false, "41", null, null, null, false), true, item));
        Assert.True(MicModel.LiveSwitchNeeded(new MicStatus(true, true, "41", null, null, null, false), true, item));
    }
}

public class MicControllerTests
{
    private sealed class Store : IMicChoiceStore
    {
        public PreferredMic Choice { get; set; } = new();
        public PreferredMic LoadMicChoice() => Choice;
        public void SaveMicChoice(PreferredMic choice) => Choice = choice;
    }

    private readonly Store _store = new();
    private readonly List<(bool On, PreferredMic Preferred)> _sent = [];
    private int _opened;
    private SessionStatus _fresh = new() { State = "recording", Mic = new MicStatus(true, false, null, null, null, null, false) };
    private Func<bool, PreferredMic, Task>? _setMicOn;

    private MicController Controller() => new(
        _store,
        (on, p, _) =>
        {
            _sent.Add((on, p));
            return _setMicOn?.Invoke(on, p) ?? Task.CompletedTask;
        },
        _ => Task.FromResult(_fresh),
        openSources: () => _opened++);

    [Fact]
    public async Task Toggle_WithoutAChoice_OpensTheSourcesAndSendsNothing()
    {
        using var c = Controller();
        Assert.Equal(MicResult.Choose, await c.ToggleAsync(true, TestContext.Current.CancellationToken));
        Assert.Empty(_sent);
        Assert.Equal(1, _opened);
        Assert.True(c.Prompt);
    }

    [Fact]
    public async Task Choose_AfterThePrompt_SavesAndTurnsOn()
    {
        using var c = Controller();
        await c.ToggleAsync(true, TestContext.Current.CancellationToken);
        var item = new MicSource("41", "alsa_input.usb", "USB headset", false);
        Assert.Equal(MicResult.On, await c.ChooseAsync(item, TestContext.Current.CancellationToken));
        Assert.Equal(new PreferredMic("alsa_input.usb", "41", "USB headset"), _store.Choice);
        Assert.Equal((true, _store.Choice), Assert.Single(_sent));
        Assert.False(c.Prompt);
    }

    [Fact]
    public async Task Choose_WhileOff_WithoutAPrompt_OnlySaves()
    {
        using var c = Controller();
        Assert.Equal(MicResult.Saved, await c.ChooseAsync(new MicSource("41", "a", "A", false), TestContext.Current.CancellationToken));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Choose_WhileLive_SwitchesToAnotherDevice()
    {
        _fresh = _fresh with { Mic = new MicStatus(true, true, "41", "alsa_input.usb", "USB headset", "capturing", false) };
        using var c = Controller();
        Assert.Equal(MicResult.On, await c.ChooseAsync(new MicSource("42", "alsa_input.pci", "Laptop mic", false), TestContext.Current.CancellationToken));
        Assert.Equal(MicResult.Saved, await c.ChooseAsync(new MicSource("42", "alsa_input.usb", "USB headset", false), TestContext.Current.CancellationToken));
        Assert.Single(_sent);
    }

    [Fact]
    public async Task Choose_WhileStarting_OnlySaves()
    {
        _fresh = new SessionStatus { State = "starting", Mic = new MicStatus(true, true, "41", "x", null, null, false) };
        using var c = Controller();
        await c.ToggleAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(MicResult.Saved, await c.ChooseAsync(new MicSource("42", "y", "Y", false), TestContext.Current.CancellationToken));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Toggle_MissingDevice_ReportsAndKeepsTheInventory()
    {
        _store.Choice = new PreferredMic("alsa_input.usb", "41", "USB headset");
        MicItem[] fresh = [new("42", "Laptop mic", "alsa_input.pci")];
        _setMicOn = (_, p) => throw new ControlException(MicModel.MissingText(p), code: "microphone_missing", microphones: fresh);
        using var c = Controller();
        Assert.Equal(MicResult.Missing, await c.ToggleAsync(true, TestContext.Current.CancellationToken));
        Assert.True(c.Missing);
        Assert.Same(fresh, c.Mics);
        c.UpdateStatus(_fresh, true);
        Assert.Equal("USB headset is not connected", c.View().Hint);
    }

    [Fact]
    public async Task LaterOff_SupersedesAPendingOn()
    {
        _store.Choice = new PreferredMic("alsa_input.usb", "41", "USB headset");
        var gate = new TaskCompletionSource();
        var first = true;
        _setMicOn = (_, _) =>
        {
            if (!first)
                return Task.CompletedTask;
            first = false;
            return gate.Task;
        };
        using var c = Controller();
        var off1 = c.ToggleAsync(false, TestContext.Current.CancellationToken);   // in flight
        var on = c.ToggleAsync(true, TestContext.Current.CancellationToken);      // queued
        var off2 = c.ToggleAsync(false, TestContext.Current.CancellationToken);  // queued, later
        gate.SetResult();
        Assert.Equal(MicResult.Off, await off1);
        Assert.Equal(MicResult.Superseded, await on);
        Assert.Equal(MicResult.Off, await off2);
        Assert.Equal([false, false], _sent.Select(s => s.On));
    }

    [Fact]
    public void SetInventory_MigratesALegacyChoice()
    {
        _store.Choice = new PreferredMic("", "41", "USB headset");
        using var c = Controller();
        c.SetInventory([new MicItem("41", "USB headset", "alsa_input.usb")]);
        Assert.Equal("alsa_input.usb", _store.Choice.StableId);
    }

    [Fact]
    public void View_ChecksTheSavedChoice()
    {
        _store.Choice = new PreferredMic("alsa_input.pci");
        using var c = Controller();
        c.SetInventory([new MicItem("41", "USB headset", "alsa_input.usb"), new MicItem("42", "", "alsa_input.pci")]);
        Assert.Equal([("USB headset", false), ("42", true)], c.View().Sources.Select(s => (s.Label, s.Checked)));
    }
}
