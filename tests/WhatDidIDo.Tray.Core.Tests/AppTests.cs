using System.Text.Json;
using WhatDidIDo.Tray.Core.App;
using WhatDidIDo.Tray.Core.Control;
using WhatDidIDo.Tray.Core.Models;

namespace WhatDidIDo.Tray.Core.Tests;

/// <summary>A synthetic TimesheetDay in UTC (zone America/New_York, so 17:00Z is 1:00 PM).</summary>
internal static class Days
{
    public const string Json = """
        {"date": "2026-10-15", "zone": "America/New_York", "now": "2026-10-15T17:40:00Z",
         "span": {"start": "2026-10-15T17:05:00Z", "end": "2026-10-15T17:40:00Z"},
         "segments": [
           {"start": "2026-10-15T17:05:00Z", "end": "2026-10-15T17:20:00Z", "state": "active",
            "window": {"app_id": "C:\\Windows\\notepad.exe", "app_name": "Notepad", "title": "notes.txt"}},
           {"start": "2026-10-15T17:20:00Z", "end": "2026-10-15T17:25:00Z", "state": "paused", "window": null},
           {"start": "2026-10-15T17:25:00Z", "end": "2026-10-15T17:40:00Z", "state": "active",
            "window": {"app_id": "", "app_name": "ConnectWise Manage", "title": "#4101"}}],
         "entries": [
           {"assignment": {"ticket": "#4101", "client": "Acme", "category": "Support"},
            "clip_start": "2026-10-15T17:05:00Z", "clip_end": "2026-10-15T17:20:00Z"}],
         "unassigned": [
           {"start": "2026-10-15T17:20:00Z", "end": "2026-10-15T17:25:00Z", "state": "paused"},
           {"start": "2026-10-15T17:25:00.000Z", "end": "2026-10-15T17:40:00.000Z", "state": "active"}],
         "suggestions": {"tickets": ["#4101", "#20591"],
           "ticket_defaults": [{"ticket": "#4101", "client": "Acme", "category": "Support", "billable": false}]}}
        """;

    public static TimesheetDay Day() => TimesheetDay.Parse(JsonDocument.Parse(Json).RootElement);
}

public class AppUrlsTests
{
    [Theory]
    [InlineData(307, "http://127.0.0.1:18803/app/#/today", "http://127.0.0.1:18803")]
    [InlineData(302, "http://127.0.0.1:18803/app/", "http://127.0.0.1:18803")]
    [InlineData(303, "/app/#/today", "http://127.0.0.1:18801")]
    [InlineData(307, "http://[::1]:18803/app/", null)]
    [InlineData(307, "http://localhost:18803/app/", null)]
    [InlineData(307, "https://127.0.0.1:18803/app/", null)]
    [InlineData(307, "http://127.0.0.1/app/", null)]
    [InlineData(307, "/day", null)]
    [InlineData(200, "http://127.0.0.1:18803/app/", null)]
    [InlineData(301, "http://127.0.0.1:18803/app/", null)]
    [InlineData(307, null, null)]
    public void AppBaseFromLocation(int status, string? location, string? expected) =>
        Assert.Equal(expected, AppUrls.AppBaseFromLocation("http://127.0.0.1:18801", status, location));

    [Theory]
    [InlineData("", true, null, false)]
    [InlineData("  ", true, null, false)]
    [InlineData("http://127.0.0.1:18803", false, "http://127.0.0.1:18803", false)]
    [InlineData(" http://127.0.0.1:18803/ ", false, "http://127.0.0.1:18803", false)]
    [InlineData("http://127.0.0.1:18803/app/", false, null, true)]
    [InlineData("http://[::1]:18803", false, null, true)]
    [InlineData("http://127.0.0.1", false, null, true)]
    [InlineData("example.org", false, null, true)]
    public void ParseSetting(string text, bool auto, string? origin, bool invalid) =>
        Assert.Equal(new AppUrlSetting(auto, origin, invalid), AppUrls.ParseSetting(text));

    [Fact]
    public void Route_EncodesLikeUrlSearchParams()
    {
        Assert.Equal("#/today", AppUrls.Route("today"));
        Assert.Equal("#/timeline?date=2026-10-15&t=10%3A45", AppUrls.Route("timeline", [new("date", "2026-10-15"), new("t", "10:45")]));
        Assert.Equal("#/timesheet?q=a+b%26c%C3%BC*-._", AppUrls.Route("timesheet", [new("q", "a b&cü*-._"), new("empty", "")]));
    }
}

public class TimesheetDayTests
{
    [Fact]
    public void Parse_ReadsWhatTheFlyoutUses()
    {
        var day = Days.Day();
        Assert.Equal("2026-10-15", day.Date);
        Assert.Equal(3, day.Segments.Count);
        Assert.Equal(new FocusWindow("C:\\Windows\\notepad.exe", "Notepad", "notes.txt"), day.Segments[0].Window);
        Assert.Null(day.Segments[1].Window);
        Assert.Equal(new DayAssignment("#4101", "Acme", "Support"), day.Entries[0].Assignment);
        // Instants sent back in an Assign keep the server's own spelling.
        Assert.Equal("2026-10-15T17:25:00.000Z", day.Unassigned[1].Start);
        Assert.Equal(["#4101", "#20591"], day.Tickets);
        Assert.Equal(new TicketDefault("#4101", "Acme", "Support", false), Assert.Single(day.TicketDefaults));
    }

    [Theory]
    [InlineData("""{"date": "2026-10-15"}""")]
    [InlineData("""{"date": "2026-10-15", "zone": "UTC", "now": "not a time", "span": null, "segments": [], "entries": [], "unassigned": []}""")]
    [InlineData("""{"date": "2026-10-15", "zone": "UTC", "now": "2026-10-15T00:00:00Z", "span": null, "segments": {}, "entries": [], "unassigned": []}""")]
    public void Parse_RefusesMalformedDays(string json)
    {
        var e = Assert.Throws<AppException>(() => TimesheetDay.Parse(JsonDocument.Parse(json).RootElement));
        Assert.Equal("invalid", e.Code);
    }
}

public class DayModelTests
{
    [Fact]
    public void CurrentStretch_LastEntry_LastFocus()
    {
        var day = Days.Day();
        Assert.Equal("2026-10-15T17:25:00.000Z", DayModel.CurrentStretch(day)!.Start);
        Assert.Equal("#4101", DayModel.LastEntry(day)!.Assignment.Ticket);
        Assert.Equal("ConnectWise Manage", DayModel.LastFocus(day)!.Window!.AppName);
        // A stretch that ended more than a minute before now is not current.
        Assert.Null(DayModel.CurrentStretch(day with { Now = day.Now.AddMinutes(2) }));
        // A paused run up to now is not a stretch either.
        Assert.Null(DayModel.CurrentStretch(day with { Unassigned = [day.Unassigned[0] with { End = "2026-10-15T17:40:00Z" }] }));
    }

    [Fact]
    public void SparkBuckets_FifteenMinutesFromTheFlooredSpanStart()
    {
        var buckets = DayModel.SparkBuckets(Days.Day());
        Assert.Equal(3, buckets.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T17:00:00Z", System.Globalization.CultureInfo.InvariantCulture), buckets[0].Start);
        // 17:00-17:15: 10 min active, covered by #4101 at 17:07:30.
        Assert.Equal(("active", "#4101"), (buckets[0].State, buckets[0].Ticket));
        Assert.Equal(10 / 15.0, buckets[0].Active, 6);
        // 17:15-17:30: 5 active, 5 paused, 5 active; ties go to the first seen; 17:22:30 is not covered.
        Assert.Equal(("active", null), (buckets[1].State, buckets[1].Ticket));
        Assert.Equal(10 / 15.0, buckets[1].Active, 6);
        Assert.Equal(("active", null), (buckets[2].State, buckets[2].Ticket));
        Assert.Empty(DayModel.SparkBuckets(Days.Day() with { Span = null }));
    }

    [Theory]
    [InlineData("ConnectWise Manage", "CM")]
    [InlineData("firefox", "Fi")]
    [InlineData("x", "X")]
    [InlineData("", "?")]
    [InlineData("my_app.exe", "MA")]
    public void AppInitials(string name, string expected) => Assert.Equal(expected, DayModel.AppInitials(name));

    // Expected slots from an independent Python port of the app's paletteSlot.
    [Theory]
    [InlineData("#4101", 1)]
    [InlineData(" #4101 ", 1)]
    [InlineData("20591", 5)]
    [InlineData("ACME-12", 5)]
    [InlineData("Ticket ü", 2)]
    [InlineData("\U0001F600x", 3)]
    [InlineData("#20592", 2)]
    [InlineData("CW-7", 4)]
    public void PaletteSlot_MatchesTheApp(string ticket, int slot) => Assert.Equal(slot, DayModel.PaletteSlot(ticket));

    [Fact]
    public void TicketColor_WinPalette()
    {
        Assert.Equal("#8764b8", DayModel.TicketColor("#4101", dark: false));
        Assert.Equal("#b4a0ff", DayModel.TicketColor("4101", dark: true));
        Assert.Equal(DayModel.NeutralLight, DayModel.TicketColor("", dark: false));
        Assert.Equal(DayModel.NeutralDark, DayModel.TicketColor(" # ", dark: true));
    }

    [Fact]
    public void ClockTime_InTheDaysZone()
    {
        var at = DateTimeOffset.Parse("2026-10-15T21:14:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("5:14 PM", DayModel.ClockTime(at, "America/New_York", use24h: false));
        Assert.Equal("17:14", DayModel.ClockTime(at, "America/New_York", use24h: true));
        Assert.Equal("17:14", DayModel.Hhmm(at, "America/New_York"));
        Assert.Equal(DayModel.ClockTime(at, null, true), DayModel.ClockTime(at, "Not/AZone", true));
    }

    [Fact]
    public void Uses24Hour()
    {
        Assert.False(DayModel.Uses24Hour(System.Globalization.CultureInfo.GetCultureInfo("en-US")));
        Assert.True(DayModel.Uses24Hour(System.Globalization.CultureInfo.GetCultureInfo("de-DE")));
    }
}

public class NowTodayTests
{
    private static readonly SessionStatus Recording = new() { State = "recording", Monitors = 1, IntervalSeconds = 5 };

    [Fact]
    public void Now_WithAStretch()
    {
        var day = Days.Day();
        var now = PopoverModel.Now(day, hasBase: true, recording: true, use24h: false, new PopoverUi());
        Assert.True(now.Visible);
        Assert.False(now.Dimmed);
        Assert.Equal(new NowFocus("", "ConnectWise Manage", "#4101", "CM", "ConnectWise Manage: #4101. Open the timesheet", true,
            AppLink.Of("timesheet", ("date", "2026-10-15"))), now.Focus);
        var s = now.Stretch!;
        Assert.Equal("1:25 PM – now is unassigned", s.Text);
        Assert.Equal("Choose ticket…", s.TicketLabel);
        Assert.Equal("Choose ticket", s.TicketAccessibleName);
        Assert.Equal(["#4101 · Acme", "#20591"], s.Tickets.Select(t => t.Label));
        Assert.False(s.AssignSensitive);
        Assert.Null(s.Assign);
        Assert.Null(now.Last);
    }

    [Fact]
    public void Now_PickedTicket_BuildsTheExactBody()
    {
        var day = Days.Day();
        var picked = PopoverModel.Now(day, true, true, true, new PopoverUi(Ticket: "#4101", Expanded: "ticket")).Stretch!;
        Assert.True(picked.TicketExpanded);
        Assert.Equal("Ticket: #4101", picked.TicketAccessibleName);
        Assert.True(picked.AssignSensitive);
        Assert.Equal(new AssignmentBody("2026-10-15T17:25:00.000Z", "2026-10-15T17:40:00.000Z", "#4101", "Acme", "Support", "", false), picked.Assign);
        // Without defaults: "", "", billable.
        Assert.Equal(new AssignmentBody("2026-10-15T17:25:00.000Z", "2026-10-15T17:40:00.000Z", "#20591", "", "", "", true),
            PopoverModel.Now(day, true, true, true, new PopoverUi(Ticket: "#20591")).Stretch!.Assign);
        var busy = PopoverModel.Now(day, true, true, true, new PopoverUi(Ticket: "#4101", Expanded: "ticket", AssignBusy: true)).Stretch!;
        Assert.False(busy.AssignSensitive || busy.TicketExpanded || busy.TicketSensitive);
        Assert.False(PopoverModel.Now(day, hasBase: false, true, true, new PopoverUi(Ticket: "#4101")).Stretch!.AssignSensitive);
    }

    [Fact]
    public void Now_WithoutAStretch_ShowsTheLastEntry_AndDimsWhenNotRecording()
    {
        var day = Days.Day() with { Unassigned = [] };
        var now = PopoverModel.Now(day, true, recording: false, use24h: true, new PopoverUi(AssignError: "Couldn't assign that time."));
        Assert.Null(now.Stretch);
        Assert.Equal("#4101 until 13:20", now.Last);
        Assert.True(now.Dimmed);
        Assert.Equal("Couldn't assign that time.", now.Error);
        Assert.False(PopoverModel.Now(null, true, true, false, new PopoverUi()).Visible);
    }

    [Fact]
    public void Today_Bars()
    {
        var day = Days.Day() with
        {
            Segments = [.. Days.Day().Segments, new DaySegment(
                DateTimeOffset.Parse("2026-10-15T16:50:00Z", System.Globalization.CultureInfo.InvariantCulture),
                DateTimeOffset.Parse("2026-10-15T17:00:00Z", System.Globalization.CultureInfo.InvariantCulture), "paused", null)],
            Span = (DateTimeOffset.Parse("2026-10-15T16:50:00Z", System.Globalization.CultureInfo.InvariantCulture), Days.Day().Span!.Value.End),
        };
        var today = PopoverModel.Today(day, hasBase: true, use24h: false, dark: false, hoverBar: null);
        Assert.True(today.Visible);
        Assert.Equal("12:50 PM – now", today.Range);
        Assert.Equal(["paused", "ticket", "accent", "accent"], today.Bars.Select(b => b.Kind));
        Assert.Equal(PopoverModel.WarnColor(false), today.Bars[0].Color);
        Assert.Equal(0.22, today.Bars[0].Height);
        Assert.Equal("#8764b8", today.Bars[1].Color);
        Assert.Null(today.Bars[2].Color);
        Assert.Equal(0.3 + 0.7 * 10 / 15.0, today.Bars[1].Height, 6);
        Assert.Equal("1:00 PM · #4101", today.Bars[1].Label);
        Assert.Equal("1:15 PM: unassigned", today.Bars[2].AccessibleName);
        Assert.Equal(AppLink.Of("timeline", ("date", "2026-10-15"), ("t", "13:07")), today.Bars[1].Link);
        Assert.Equal("1:15 PM · unassigned", PopoverModel.Today(day, true, false, false, hoverBar: 2).Label);
        Assert.False(PopoverModel.Today(null, true, false, false, null).Visible);
    }

    [Fact]
    public void View_LinksAndTheInvalidSetting()
    {
        var view = PopoverModel.View(Recording with { DescribeWithheld = 2 },
            app: new AppState(HasBase: true, Day: Days.Day()), localNow: DateTimeOffset.Parse("2026-10-16T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(view.Open.Sensitive);
        Assert.True(view.Tiles.Phi.Sensitive);
        Assert.Equal(AppLink.Of("timeline", ("date", "2026-10-15"), ("phi", "1")), view.PhiLink);
        var none = PopoverModel.View(Recording, app: new AppState(InvalidSetting: true),
            localNow: new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 16))));
        Assert.False(none.Open.Sensitive);
        Assert.Equal(["App server URL in Settings is not a local address"], none.Notices);
        Assert.Equal(AppLink.Of("timeline", ("date", "2026-10-16"), ("phi", "1")), none.PhiLink);
        Assert.False(none.Now.Visible || none.Today.Visible);
    }

    [Fact]
    public void AssignErrorText()
    {
        Assert.Equal("The timesheet changed. Try again.", PopoverModel.AssignErrorText(new AppException("x", 409, "conflict")));
        Assert.Equal("Couldn't assign that time.", PopoverModel.AssignErrorText(new AppException("x", 500)));
    }
}

public sealed class AppClientTests : IAsyncDisposable
{
    private const string Token = "tok_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly FakeControlServer _control = new(Token);
    private readonly FakeControlServer _app = new(Token) { ControlRules = false };
    private readonly MemoryControlFile _file = new();
    private readonly ControlClient _controlClient;
    private string _setting = "";
    private readonly AppClient _client;

    public AppClientTests()
    {
        _file.Text = _control.ControlJson();
        _controlClient = new ControlClient(_file);
        _client = new AppClient(_controlClient, () => _setting);
        _control.Handler = (_, p, _) => p == "/day" ? (307, "{}") : (404, "{}");
        _control.ResponseHeaders = p => p == "/day" ? new Dictionary<string, string> { ["Location"] = $"{_app.BaseUrl}/app/#/today" } : null;
        _app.Handler = (m, p, _) => m == "GET" && p == "/api/v1/timesheet/day?date=2026-10-15" ? (200, Days.Json) : (404, "{}");
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        _controlClient.Dispose();
        await _control.DisposeAsync();
        await _app.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Forbidden = ["authorization", "origin", "cookie", "sec-fetch-site", "sec-fetch-mode", "sec-fetch-dest"];

    [Fact]
    public async Task Discovery_FromTheDayRedirect_WithoutTheToken()
    {
        Assert.Equal(_app.BaseUrl, await _client.BaseAsync(Ct));
        var probe = Assert.Single(_control.Requests);
        Assert.Equal("/day", probe.Target);
        Assert.DoesNotContain(probe.Headers.Keys, Forbidden.Contains);
        // Cached until the control base_url changes.
        await _client.BaseAsync(Ct);
        Assert.Single(_control.Requests);
        Assert.Equal($"{_app.BaseUrl}/app/#/timeline?date=2026-10-15", _client.Url("timeline", [new("date", "2026-10-15")]));
    }

    [Fact]
    public async Task Discovery_RelativeLocation_IsTheControlServer()
    {
        _control.ResponseHeaders = _ => new Dictionary<string, string> { ["Location"] = "/app/#/today" };
        Assert.Equal(_control.BaseUrl, await _client.BaseAsync(Ct));
    }

    [Fact]
    public async Task Discovery_NonLoopback_OrNoRedirect_IsNoAppServer()
    {
        _control.ResponseHeaders = _ => new Dictionary<string, string> { ["Location"] = "http://example.org/app/" };
        Assert.Null(await _client.BaseAsync(Ct));
        Assert.Null(await _client.DayAsync("2026-10-15", Ct));
        Assert.Null(_client.Url("today"));
        Assert.Empty(_app.Requests);
    }

    [Fact]
    public async Task Discovery_KeepsTheLastOrigin_WhileTheSessionIsNotRunning()
    {
        await _client.BaseAsync(Ct);
        _file.Text = null;
        Assert.Equal(_app.BaseUrl, await _client.BaseAsync(Ct));
    }

    [Fact]
    public async Task Setting_OverridesDiscovery()
    {
        _setting = _app.BaseUrl + "/";
        Assert.Equal(_app.BaseUrl, await _client.BaseAsync(Ct));
        Assert.Empty(_control.Requests);
        _setting = "http://[::1]:1";
        Assert.True(_client.InvalidSetting);
        Assert.Null(await _client.BaseAsync(Ct));
    }

    [Fact]
    public async Task Day_PlainLoopbackRequest()
    {
        var day = await _client.DayAsync("2026-10-15", Ct);
        Assert.Equal("2026-10-15", day!.Date);
        var r = Assert.Single(_app.Requests);
        Assert.DoesNotContain(r.Headers.Keys, Forbidden.Contains);
        Assert.Equal($"127.0.0.1:{_app.Port}", r.Headers["host"]);
        await Assert.ThrowsAsync<AppException>(() => _client.DayAsync("15/10/2026", Ct));
    }

    [Fact]
    public async Task Day_404_IsNull_ARedirect_IsAFailure()
    {
        Assert.Null(await _client.DayAsync("2026-10-16", Ct));
        _app.Handler = (_, _, _) => (302, "{}");
        _app.ResponseHeaders = _ => new Dictionary<string, string> { ["Location"] = "http://example.org/" };
        var e = await Assert.ThrowsAsync<AppException>(() => _client.DayAsync("2026-10-15", Ct));
        Assert.Equal(302, e.Status);
        Assert.Single(_app.Requests, r => r.Target.Contains("2026-10-15", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Assign_ExactKeys_AndTheErrorEnvelope()
    {
        _app.Handler = (m, _, _) => m == "POST" ? (201, """{"assignment": {}}""") : (404, "{}");
        await _client.AssignAsync(new AssignmentBody("a", "b", "#1", "Acme", "Support", "", false), Ct);
        var post = _app.Requests.Single(r => r.Method == "POST");
        Assert.Equal("/api/v1/timesheet/assignments", post.Target);
        Assert.Equal("""{"start":"a","end":"b","ticket":"#1","client":"Acme","category":"Support","note":"","billable":false}""", post.Body);
        Assert.Equal("application/json", post.Headers["content-type"]);

        _app.Handler = (_, _, _) => (409, """{"error": {"code": "conflict", "message": "changed"}}""");
        var e = await Assert.ThrowsAsync<AppException>(() => _client.AssignAsync(new AssignmentBody("a", "b", "#1", "", "", "", true), Ct));
        Assert.Equal((409, "conflict"), (e.Status, e.Code));
    }

    [Fact]
    public async Task Unreachable_ForgetsTheDiscovery()
    {
        await _client.BaseAsync(Ct);
        await _app.DisposeAsync();
        var e = await Assert.ThrowsAsync<AppException>(() => _client.DayAsync("2026-10-15", Ct));
        Assert.True(e.Unreachable);
        await _client.BaseAsync(Ct);
        Assert.Equal(2, _control.Requests.Count(r => r.Target == "/day"));
    }
}
