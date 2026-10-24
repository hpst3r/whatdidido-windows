using WhatDidIDo.Tray.Core.Control;
using WhatDidIDo.Tray.Core.Models;

namespace WhatDidIDo.Tray.Core.Tests;

/// <summary>Ported from the extension's tests/mic_reminder_test.js (same clock origin and cases).</summary>
public class MicReminderTests
{
    private const long M = PauseReminder.MinuteMs;
    private const long T0 = 7_654_321;   // the monotonic clock never starts at 0

    private static readonly ReminderEffect None = ReminderEffect.None;
    private static readonly ReminderEffect Withdraw = ReminderEffect.WithdrawShown;

    // A recording status with the microphone on and transcription enabled.
    private static SessionStatus St(int withText = 0) => new()
    {
        State = "recording",
        TranscriptionEnabled = true,
        TranscriptionWithText = withText,
        Mic = new MicStatus(true, true, "m2", "alsa_input.usb", "USB headset", "capturing", true),
    };

    private static SessionStatus WithMic(SessionStatus s, Func<MicStatus, MicStatus> change) => s with { Mic = change(s.Mic!) };

    private static ReminderEffect Shows(long minutes, string label = "USB headset") =>
        new(MicReminder.Body(label, minutes * M), false);

    [Fact]
    public void Texts()
    {
        Assert.Equal("Check your microphone", MicReminder.Title);
        Assert.Equal("No speech transcribed from USB headset for 1 minute.", MicReminder.Body("USB headset", 2 * M - 1));
        Assert.Equal("No speech transcribed from USB headset for 10 minutes.", MicReminder.Body("USB headset", 10 * M + 59999));
        Assert.Equal("No speech transcribed from the microphone for 3 minutes.", MicReminder.Body(null, 3 * M));
        Assert.Equal("No speech transcribed from the microphone for 3 minutes.", MicReminder.Body("", 3 * M));
    }

    [Fact]
    public void Armed()
    {
        Assert.True(MicReminder.Armed(St(), 10));
        foreach (var state in new[] { "waiting_for_device", "retrying", "pending", null })
            Assert.True(MicReminder.Armed(WithMic(St(), m => m with { State = state }), 10));
        foreach (var state in new[] { "limit_reached", "failed" })
            Assert.False(MicReminder.Armed(WithMic(St(), m => m with { State = state }), 10));
        Assert.False(MicReminder.Armed(St() with { AudioOutputMonitor = true }, 10));
        Assert.True(MicReminder.Armed(WithMic(St(), m => m with { StableId = null, Label = null, State = null }), 10));
        Assert.False(MicReminder.Armed(null, 10));
        Assert.False(MicReminder.Armed(St(), 0));
        Assert.False(MicReminder.Armed(St() with { TranscriptionWithText = null }, 10));
        Assert.False(MicReminder.Armed(St() with { TranscriptionEnabled = false }, 10));
        foreach (var state in new[] { "paused", "recovering", "stopped", "stopping", "starting", "failed", null })
            Assert.False(MicReminder.Armed(St() with { State = state }, 10));
        Assert.False(MicReminder.Armed(St() with { WaitingForUnlock = true }, 10));
        Assert.False(MicReminder.Armed(WithMic(St(), m => m with { On = false }), 10));
        Assert.False(MicReminder.Armed(St() with { Mic = null }, 10));
    }

    [Fact]
    public void FirstReminderAndRepeats()
    {
        var r = new MicReminder(10);
        Assert.Equal(PauseReminder.DefaultMinutes, new MicReminder().Minutes);
        Assert.Equal((null, null, None), (r.QuietSince, r.DueAt, r.Fire(T0 + 99 * M)));
        Assert.Equal(None, r.Observe(St(4), T0 + 5));
        Assert.Equal(T0 + 5, r.QuietSince);
        Assert.Equal(T0 + 5 + 10 * M, r.DueAt);
        Assert.Equal(None, r.Observe(St(4), T0 + 4 * M));
        Assert.Equal(None, r.Fire(T0 + 5 + 10 * M - 1));
        Assert.Equal(Shows(10), r.Fire(T0 + 5 + 10 * M));
        Assert.Equal(T0 + 5 + 20 * M, r.DueAt);
        Assert.Equal(None, r.Fire(T0 + 5 + 10 * M));
        Assert.Equal(Shows(21), r.Fire(T0 + 5 + 21 * M));
        Assert.Equal(Shows(45), r.Observe(St(4), T0 + 5 + 45 * M));
        Assert.Equal(Shows(55, "Renamed"), r.Observe(WithMic(St(4), m => m with { Label = "Renamed" }), T0 + 5 + 55 * M));
        Assert.Equal(Shows(65, "the microphone"), r.Observe(WithMic(St(4), m => m with { Label = null }), T0 + 5 + 65 * M));
    }

    [Fact]
    public void Speech_RestartsTheCount()
    {
        var r = new MicReminder(10);
        r.Observe(St(4), T0);
        Assert.Equal(None, r.Observe(St(5), T0 + 3 * M));
        Assert.Equal((T0 + 3 * M, T0 + 13 * M), (r.QuietSince, r.DueAt));
        Assert.Equal(Shows(10), r.Fire(T0 + 13 * M));
        Assert.Equal(Withdraw, r.Observe(St(7), T0 + 15 * M));
        Assert.Equal(T0 + 25 * M, r.DueAt);
    }

    [Fact]
    public void ServerRestart_Rebaselines()
    {
        var r = new MicReminder(10);
        r.Observe(St(40), T0);
        r.Fire(T0 + 10 * M);
        Assert.Equal(Withdraw, r.Observe(St(0), T0 + 12 * M));
        Assert.Equal(None, r.Observe(St(0), T0 + 13 * M));
        Assert.Equal(None, r.Observe(St(1), T0 + 14 * M));
        Assert.Equal(T0 + 14 * M, r.QuietSince);
    }

    [Fact]
    public void AnotherMicrophone_RestartsTheCount()
    {
        var r = new MicReminder(10);
        r.Observe(St(4), T0);
        r.Fire(T0 + 10 * M);
        var builtIn = WithMic(St(4), m => m with { StableId = "alsa_input.pci", SourceId = "m1", Label = "Built-in" });
        Assert.Equal(Withdraw, r.Observe(builtIn, T0 + 11 * M));
        Assert.Equal(Shows(10, "Built-in"), r.Fire(T0 + 21 * M));

        r = new MicReminder(10);
        r.Observe(St(4), T0);
        Assert.Equal(None, r.Observe(WithMic(St(4), m => m with { SourceId = "m9" }), T0 + 2 * M));
        Assert.Equal(T0, r.QuietSince);

        // Older servers: no stable id, the source id is the identity.
        static SessionStatus Old(string source) => WithMic(St(4), m => m with { SourceId = source, StableId = null, Label = null, State = null });
        r = new MicReminder(10);
        r.Observe(Old("m1"), T0);
        r.Observe(Old("m1"), T0 + M);
        Assert.Equal(T0, r.QuietSince);
        r.Observe(Old("m2"), T0 + 2 * M);
        Assert.Equal(T0 + 2 * M, r.QuietSince);
    }

    public static TheoryData<string, SessionStatus?> Disarming => new()
    {
        { "not running", null },
        { "mic off", WithMic(St(4), m => m with { On = false }) },
        { "paused", St(4) with { State = "paused" } },
        { "waiting for unlock", St(4) with { WaitingForUnlock = true } },
        { "transcription off", St(4) with { TranscriptionEnabled = false, TranscriptionWithText = null } },
        { "audio limit reached", WithMic(St(4), m => m with { State = "limit_reached" }) },
        { "output monitor", St(4) with { AudioOutputMonitor = true } },
    };

    [Theory]
    [MemberData(nameof(Disarming))]
    public void Disarming_WithdrawsAndCancels(string name, SessionStatus? status)
    {
        var r = new MicReminder(10);
        r.Observe(St(4), T0);
        r.Fire(T0 + 10 * M);
        Assert.Equal(Withdraw, r.Observe(status, T0 + 11 * M));
        Assert.Equal((null, null, None), (r.QuietSince, r.DueAt, r.Fire(T0 + 30 * M)));
        Assert.Equal(None, r.Observe(status, T0 + 12 * M));
        Assert.Equal(None, r.Observe(St(4), T0 + 13 * M));
        Assert.True(r.DueAt == T0 + 23 * M, name);
    }

    [Fact]
    public void StopReminding_UntilSpeechOrAnotherMicOrDisarm()
    {
        var r = new MicReminder(10);
        Assert.Equal(None, r.Suppress());
        r.Observe(St(4), T0);
        Assert.Equal(T0 + 10 * M, r.DueAt);
        r.Fire(T0 + 10 * M);
        Assert.Equal(Withdraw, r.Suppress());
        Assert.Null(r.DueAt);
        Assert.Equal(None, r.Observe(St(4), T0 + 100 * M));
        Assert.Equal(None, r.SetMinutes(1, T0 + 101 * M));
        Assert.Equal(None, r.Observe(St(5), T0 + 102 * M));
        Assert.Equal(T0 + 103 * M, r.DueAt);

        r = new MicReminder(10);
        r.Observe(St(4), T0);
        r.Suppress();
        r.Observe(WithMic(St(4), m => m with { StableId = "other" }), T0 + M);
        Assert.Equal(T0 + 11 * M, r.DueAt);

        r = new MicReminder(10);
        r.Observe(St(4), T0);
        r.Suppress();
        r.Observe(WithMic(St(4), m => m with { On = false }), T0 + M);
        r.Observe(St(4), T0 + 2 * M);
        Assert.Equal(T0 + 12 * M, r.DueAt);
    }

    [Fact]
    public void ChangingMinutes()
    {
        var r = new MicReminder(10);
        r.Observe(St(4), T0);
        Assert.Equal(None, r.SetMinutes(20, T0 + 3 * M));
        Assert.Equal(T0 + 20 * M, r.DueAt);
        Assert.Equal(Shows(7), r.SetMinutes(5, T0 + 7 * M));
        Assert.Equal(T0 + 12 * M, r.DueAt);
        Assert.Equal(Withdraw, r.SetMinutes(0, T0 + 8 * M));
        Assert.Equal(None, r.Observe(St(4), T0 + 9 * M));
        Assert.Equal(None, r.SetMinutes(10, T0 + 10 * M));
        Assert.Null(r.DueAt);
        r.Observe(St(4), T0 + 11 * M);
        Assert.Equal(T0 + 21 * M, r.DueAt);
        Assert.Equal(PauseReminder.MaxMinutes, new MicReminder(999).Minutes);
        Assert.Equal(0, new MicReminder(-1).Minutes);
    }
}

public class ReminderNoticeTests
{
    [Fact]
    public void Notices()
    {
        var pause = ReminderNotice.Pause("Paused for 10 minutes.");
        Assert.Equal(("pause", "Recording is paused"), (pause.Tag, pause.Title));
        Assert.Equal(["Resume", "Stop reminding"], pause.Buttons.Select(b => b.Label));
        var mic = ReminderNotice.Mic("No speech…");
        Assert.Equal(("mic", "Check your microphone"), (mic.Tag, mic.Title));
        Assert.Equal([ReminderAction.ChooseMicrophone, ReminderAction.StopReminding], mic.Buttons.Select(b => b.Action));
    }

    [Fact]
    public void Arguments_RoundTrip()
    {
        foreach (var notice in new[] { ReminderNotice.Pause("x"), ReminderNotice.Mic("y") })
        {
            foreach (var button in notice.Buttons)
            {
                var args = ReminderNotice.Arguments(notice.Kind, button.Action).ToDictionary();
                Assert.Equal((notice.Kind, button.Action), ReminderNotice.Parse(args));
            }
        }
    }

    [Theory]
    [InlineData("pause", "ChooseMicrophone")]  // not offered by that reminder
    [InlineData("mic", "Resume")]
    [InlineData("pause", "0")]                 // a number is not a name
    [InlineData("pause", "Resume,StopReminding")]
    [InlineData("pause", "resume")]
    [InlineData("other", "Resume")]
    public void Parse_RefusesOtherArguments(string reminder, string action) =>
        Assert.Null(ReminderNotice.Parse(new Dictionary<string, string> { ["reminder"] = reminder, ["action"] = action }));

    [Fact]
    public void EncodeDecode()
    {
        var text = ReminderNotice.Encode(ReminderNotice.Arguments(ReminderKind.Mic, ReminderAction.ChooseMicrophone));
        Assert.Equal("reminder=mic;action=ChooseMicrophone", text);
        Assert.Equal((ReminderKind.Mic, ReminderAction.ChooseMicrophone), ReminderNotice.Parse(ReminderNotice.Decode(text)));
        Assert.Equal(new Dictionary<string, string> { ["a"] = "1", ["b"] = "" }, ReminderNotice.Decode("a=1;junk;b=;a=2;=x"));
        Assert.Empty(ReminderNotice.Decode(null));
    }

    [Fact]
    public void Parse_ABodyClick_IsNoAction() =>
        Assert.Null(ReminderNotice.Parse(new Dictionary<string, string>()));
}
