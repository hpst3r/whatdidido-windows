using WhatDidIDo.Tray.Core.Control;
using WhatDidIDo.Tray.Core.Models;

namespace WhatDidIDo.Tray.Core.Tests;

public class StatusModelTests
{
    private static SessionStatus Status(string state, MicStatus? mic = null) =>
        new() { State = state, Samples = 10, Pending = 0, Monitors = 1, IntervalSeconds = 5, Mic = mic };

    [Theory]
    [InlineData("stopped", true, false, false, false)]
    [InlineData("failed", true, false, false, false)]
    [InlineData("starting", false, false, false, true)]
    [InlineData("recording", false, true, false, true)]
    [InlineData("paused", false, false, true, true)]
    [InlineData("recovering", false, false, false, true)]
    [InlineData("stopping", false, false, false, false)]
    [InlineData("bogus", false, false, false, false)]
    public void Allowed_MirrorsTheServerGuards(string state, bool start, bool pause, bool resume, bool stop) =>
        Assert.Equal(new AllowedActions(start, pause, resume, stop), StatusModel.Allowed(state));

    [Fact]
    public void EveryState_HasItsOwnIcon()
    {
        var kinds = StatusModel.States.Where(s => s != "stopping")
            .Select(s => StatusModel.IconKind(s, running: true)).Append(StatusModel.IconKind(null, running: false));
        Assert.Equal(7, kinds.Distinct().Count());
        Assert.Equal(StatusModel.IconKind("starting", true), StatusModel.IconKind("stopping", true));
    }

    [Fact]
    public void Tray_NotRunning()
    {
        var view = StatusModel.Tray(null);
        Assert.Equal(new TrayView(TrayIconKind.Offline, false, "whatdidido: whatdidido not running", "whatdidido not running"), view);
    }

    [Fact]
    public void Tray_MicBadge_OnlyWhileTheMicIsLive()
    {
        var on = new MicStatus(true, true, "1", null, "USB", "capturing", false);
        Assert.Equal("whatdidido: Recording, microphone on", StatusModel.Tray(Status("recording", on)).Tooltip);
        Assert.True(StatusModel.Tray(Status("recording", on)).MicBadge);
        Assert.False(StatusModel.Tray(Status("recording", on), showMic: false).MicBadge);
        Assert.False(StatusModel.Tray(Status("recording", on with { State = "limit_reached" })).MicBadge);
        Assert.False(StatusModel.Tray(Status("recording", on with { State = "failed" })).MicBadge);
        Assert.False(StatusModel.Tray(Status("recording", on with { On = false })).MicBadge);
        Assert.Equal("whatdidido: Paused", StatusModel.Tray(Status("paused")).Tooltip);
    }

    [Theory]
    [InlineData("recording", false, 2)]
    [InlineData("starting", false, 2)]
    [InlineData("recovering", false, 2)]
    [InlineData("stopping", false, 2)]
    [InlineData("paused", false, 10)]
    [InlineData("stopped", false, 10)]
    [InlineData(null, false, 10)]
    [InlineData("paused", true, 2)]
    public void PollPeriod_FastWhileBusyOrOpen(string? state, bool open, int expected) =>
        Assert.Equal(expected, StatusModel.PollPeriod(state, open));

    [Fact]
    public void PollPeriod_IsAtLeastOneSecond() => Assert.Equal(1, StatusModel.PollPeriod("recording", false, fast: 0));

    [Fact]
    public void LastErrorText_Variants()
    {
        Assert.Null(StatusModel.LastErrorText(Status("recording")));
        Assert.Equal("Last error: boom (portal)", StatusModel.LastErrorText(Status("recording") with { LastError = "boom", Reason = "portal" }));
        Assert.Equal("Last error: boom", StatusModel.LastErrorText(Status("recording") with { LastError = "boom" }));
        Assert.Equal("Failed: capture_lost", StatusModel.LastErrorText(Status("failed") with { Reason = "capture_lost" }));
    }

    [Fact]
    public void ErrorText_Variants()
    {
        Assert.Equal("whatdidido not running", StatusModel.ErrorText(new ControlException("x", notRunning: true)));
        Assert.Equal("Not allowed in the current state", StatusModel.ErrorText(new ControlException("HTTP 409 not_allowed", 409, "not_allowed")));
        Assert.Equal("HTTP 500", StatusModel.ErrorText(new ControlException("HTTP 500", 500)));
        Assert.Equal("USB is not connected", StatusModel.ErrorText(new ControlException("USB is not connected", code: "microphone_missing")));
        Assert.Equal("request failed: timeout", StatusModel.ErrorText(new ControlException("request failed: timeout")));
    }
}

public class PopoverModelTests
{
    private static SessionStatus Status(string state) =>
        new() { State = state, Samples = 19872, Pending = 2, Monitors = 3, IntervalSeconds = 5 };

    [Theory]
    [InlineData(5, "5 s")]
    [InlineData(45, "45 s")]
    [InlineData(60, "1 min")]
    [InlineData(90, "2 min")]
    [InlineData(900, "15 min")]
    [InlineData(3600, "1 h")]
    [InlineData(5400, "1.5 h")]
    [InlineData(0, "?")]
    [InlineData(null, "?")]
    public void IntervalLabel_MatchesTheApp(int? seconds, string expected) =>
        Assert.Equal(expected, PopoverModel.IntervalLabel(seconds));

    [Theory]
    [InlineData(5, 0)]
    [InlineData(7, 0)]
    [InlineData(8, 1)]
    [InlineData(3600, 13)]
    [InlineData(99999, 13)]
    [InlineData(null, 0)]
    public void NearestStep_TiesGoToTheSmallerStop(int? seconds, int index) =>
        Assert.Equal(index, PopoverModel.NearestStep(seconds));

    [Fact]
    public void Recording()
    {
        var withMic = Status("recording") with { Mic = new MicStatus(true, true, null, null, null, null, false) };
        var view = PopoverModel.View(withMic);
        Assert.Equal("Recording", view.Status.Title);
        Assert.Equal("3 monitors · every 5 s · mic on", view.Status.Subtitle);
        Assert.Equal(StatusMark.Recording, view.Status.Mark);
        Assert.True(view.Status.Pulse);
        Assert.Equal(new PrimaryButton("pause", "Pause recording", false, true), view.Status.Primary);
        Assert.Equal(new Footer(true, "19,872 samples · 2 pending", true, false, true), view.Footer);
        Assert.Empty(view.Notices);
    }

    [Fact]
    public void Recording_WithoutAMicOnTheServer_SaysNothingAboutIt() =>
        Assert.Equal("1 monitor · every 1 min", PopoverModel.View(Status("recording") with { Monitors = 1, IntervalSeconds = 60 }).Status.Subtitle);

    [Fact]
    public void Paused()
    {
        var view = PopoverModel.View(Status("paused"), pausedMs: 12 * 60_000 + 5);
        Assert.Equal("Paused for 12 min · nothing is captured", view.Status.Subtitle);
        Assert.True(view.Status.Paused);
        Assert.Equal(StatusMark.Paused, view.Status.Mark);
        Assert.Equal(new PrimaryButton("resume", "Resume recording", true, true), view.Status.Primary);
        Assert.Equal("Nothing is captured while paused", view.Footer.Text);
        Assert.Equal("Paused for <1 min · nothing is captured", PopoverModel.View(Status("paused"), pausedMs: 59_999).Status.Subtitle);
        Assert.Equal("Nothing is captured while paused", PopoverModel.View(Status("paused")).Status.Subtitle);
    }

    [Theory]
    [InlineData("stopped", "Stopped", "Start recording when you're ready", "start")]
    [InlineData("failed", "Failed", "The session stopped after an error", "start")]
    [InlineData("starting", "Starting…", "3 monitors · every 5 s", null)]
    [InlineData("recovering", "Reconnecting…", "3 monitors · every 5 s", null)]
    [InlineData("stopping", "Stopping…", "3 monitors · every 5 s", null)]
    public void OtherStates(string state, string title, string subtitle, string? command)
    {
        var view = PopoverModel.View(Status(state));
        Assert.Equal(title, view.Status.Title);
        Assert.Equal(subtitle, view.Status.Subtitle);
        Assert.Equal(command, view.Status.Primary?.Command);
        Assert.Equal(StatusMark.Idle, view.Status.Mark);
    }

    [Fact]
    public void WaitingForUnlock() =>
        Assert.Equal("Waiting for unlock", PopoverModel.View(Status("recovering") with { WaitingForUnlock = true }).Status.Title);

    [Fact]
    public void NotRunning()
    {
        var view = PopoverModel.View(null);
        Assert.False(view.Running);
        Assert.Equal(StatusKind.NotRunning, view.Status.Kind);
        Assert.Equal(StatusMark.Power, view.Status.Mark);
        Assert.Equal("Capture not running", view.Status.Title);
        Assert.Equal(PopoverModel.NotRunningDetail, view.Status.Subtitle);
        Assert.Null(view.Status.Primary);
        Assert.False(view.Footer.Visible);
    }

    [Fact]
    public void PollError()
    {
        var view = PopoverModel.View(null, running: false, pollError: "Control token rejected");
        Assert.Equal(StatusKind.Error, view.Status.Kind);
        Assert.Equal("Can't read the capture status", view.Status.Title);
        Assert.Equal("Control token rejected", view.Status.Subtitle);
    }

    [Fact]
    public void Notices_ActionFailureThenLastError()
    {
        var view = PopoverModel.View(Status("recording") with { LastError = "boom" }, notice: "The command failed");
        Assert.Equal(["The command failed", "Last error: boom"], view.Notices);
    }

    [Fact]
    public void StopConfirmation_AndBusy()
    {
        var confirming = PopoverModel.View(Status("recording"), ui: new PopoverUi(StopConfirm: true));
        Assert.True(confirming.Footer.Confirm);
        Assert.False(confirming.Footer.StopVisible);
        var busy = PopoverModel.View(Status("recording"), ui: new PopoverUi(CommandBusy: true));
        Assert.False(busy.Status.Primary!.Sensitive);
        Assert.False(busy.Footer.StopSensitive);
        // Nothing to confirm in a state that can't stop.
        Assert.False(PopoverModel.View(Status("stopped"), ui: new PopoverUi(StopConfirm: true)).Footer.Confirm);
    }
}

public class PauseReminderTests
{
    private const long Min = PauseReminder.MinuteMs;

    [Fact]
    public void RemindsAfterMinutes_ThenEveryMinutesAfterTheLast()
    {
        var r = new PauseReminder(10);
        Assert.Equal(ReminderEffect.None, r.Observe("paused", 0));
        Assert.Equal(10 * Min, r.DueAt);
        Assert.Equal(ReminderEffect.None, r.Fire(10 * Min - 1));
        var shown = r.Fire(10 * Min);
        Assert.Equal("Paused for 10 minutes. Screenshots, audio and window activity aren't being recorded.", shown.Show);
        Assert.Equal(20 * Min, r.DueAt);
        Assert.Equal(new ReminderEffect(PauseReminder.Body(21 * Min), false), r.Observe("paused", 21 * Min));
    }

    [Fact]
    public void LeavingPaused_Withdraws()
    {
        var r = new PauseReminder(1);
        r.Observe("paused", 0);
        Assert.Equal(ReminderEffect.WithdrawShown, r.Observe("recording", 5));
        Assert.Null(r.PausedSince);
        Assert.Equal(ReminderEffect.None, r.Observe(null, 6));
    }

    [Fact]
    public void Suppress_UntilPausedIsLeft()
    {
        var r = new PauseReminder(1);
        r.Observe("paused", 0);
        Assert.Equal(ReminderEffect.WithdrawShown, r.Suppress());
        Assert.Null(r.DueAt);
        r.Observe("recording", 1);
        r.Observe("paused", 2);
        Assert.Equal(2 + Min, r.DueAt);
    }

    [Fact]
    public void SetMinutes_WhilePaused()
    {
        var r = new PauseReminder(10);
        r.Observe("paused", 0);
        Assert.NotNull(r.SetMinutes(5, 6 * Min).Show);
        Assert.Equal(ReminderEffect.WithdrawShown, r.SetMinutes(0, 7 * Min));
        Assert.Null(r.DueAt);
        Assert.Equal(PauseReminder.MaxMinutes, new PauseReminder(9999).Minutes);
    }

    [Fact]
    public void Texts()
    {
        Assert.Equal("Paused for <1 min", PauseReminder.PausedForText(0));
        Assert.Equal("Paused for 1 min", PauseReminder.PausedForText(Min));
        Assert.StartsWith("Paused for 1 minute.", PauseReminder.Body(Min), StringComparison.Ordinal);
        Assert.Equal(0, PauseReminder.TimerDelay(5, 10));
        Assert.Null(PauseReminder.TimerDelay(null, 10));
    }
}
