using System.Net;
using System.Net.Sockets;
using WhatDidIDo.Tray.Core.Control;

namespace WhatDidIDo.Tray.Core.Tests;

public sealed class ControlClientTests : IAsyncDisposable
{
    private const string Token = "tok_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string StatusJson = """
        {"state": "recording", "samples": 19872, "pending": 2, "monitors": 3, "interval_seconds": 5,
         "last_error": null, "reason": null,
         "mic": {"available": true, "on": true, "source_id": "41", "stable_id": "alsa_input.usb", "label": "USB headset", "state": "capturing"},
         "window_activity": {"available": true, "recorded": 4, "dropped": 0, "epoch": 7},
         "describe_withheld": 3, "unknown_future_key": {"x": 1}}
        """;

    private readonly FakeControlServer _server = new(Token);
    private readonly MemoryControlFile _file = new();
    private readonly ControlClient _client;

    public ControlClientTests()
    {
        _file.Text = _server.ControlJson();
        _client = new ControlClient(_file);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Status_ParsesTheStatusObject()
    {
        _server.Handler = (m, p, _) => m == "GET" && p == "/control/v1/status" ? (200, StatusJson) : (404, "{}");
        var s = await _client.StatusAsync(Ct);
        Assert.Equal("recording", s.State);
        Assert.Equal(19872, s.Samples);
        Assert.Equal(2, s.Pending);
        Assert.Equal(3, s.Monitors);
        Assert.Equal(5, s.IntervalSeconds);
        Assert.Null(s.LastError);
        Assert.Equal(new MicStatus(true, true, "41", "alsa_input.usb", "USB headset", "capturing", false), s.Mic);
        Assert.True(s.WindowActivityAvailable);
        Assert.Equal(7, s.WindowActivityEpoch);
        Assert.Equal(3, s.DescribeWithheld);
    }

    [Fact]
    public async Task Requests_CarryOnlyTheAllowedHeaders()
    {
        _server.Handler = (_, _, _) => (202, """{"state": "paused"}""");
        Assert.Equal("paused", await _client.CommandAsync("pause", Ct));
        var r = Assert.Single(_server.Requests);
        Assert.Equal("POST", r.Method);
        Assert.Equal("/control/v1/command", r.Target);
        Assert.Equal("""{"action":"pause"}""", r.Body);
        Assert.Equal("application/json", r.Headers["content-type"]);
        Assert.Equal($"Bearer {Token}", r.Headers["authorization"]);
        Assert.Equal($"127.0.0.1:{_server.Port}", r.Headers["host"]);
        Assert.StartsWith("whatdidido-windows/", r.Headers["user-agent"], StringComparison.Ordinal);
        foreach (var name in r.Headers.Keys)
            Assert.DoesNotMatch("^(origin|cookie|sec-fetch-.*|forwarded|x-forwarded-.*)$", name);
    }

    [Fact]
    public async Task Interval_SendsExactlySeconds()
    {
        _server.Handler = (_, _, _) => (200, """{"interval_seconds": 30}""");
        await _client.SetIntervalAsync(30, Ct);
        Assert.Equal("""{"seconds":30}""", Assert.Single(_server.Requests).Body);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.SetIntervalAsync(4, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.SetIntervalAsync(3601, Ct));
    }

    [Fact]
    public async Task UnknownCommand_SendsNothing()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _client.CommandAsync("delete", Ct));
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task ApiError_CarriesStatusAndCode()
    {
        _server.Handler = (_, _, _) => (409, """{"error": "not_allowed"}""");
        var e = await Assert.ThrowsAsync<ControlException>(() => _client.CommandAsync("start", Ct));
        Assert.Equal(409, e.Status);
        Assert.Equal("not_allowed", e.Code);
        Assert.Equal("HTTP 409 not_allowed", e.Message);
        Assert.False(e.NotRunning);
    }

    [Fact]
    public async Task Forbidden_RereadsTheControlFileOnce()
    {
        _server.Handler = (_, _, _) => (200, StatusJson);
        // The session restarted: new token, the tray still holds the old file text until it re-reads.
        _server.Token = "tok_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        var reads = 0;
        var file = new CallbackControlFile(() => reads++ == 0 ? _server.ControlJson(Token) : _server.ControlJson());
        using var client = new ControlClient(file);
        Assert.Equal("recording", (await client.StatusAsync(Ct)).State);
        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Forbidden_WithAnUnchangedFile_Fails()
    {
        _server.Token = "tok_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        var e = await Assert.ThrowsAsync<ControlException>(() => _client.StatusAsync(Ct));
        Assert.Equal(403, e.Status);
        Assert.Equal("forbidden", e.Code);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task NoControlFile_IsNotRunning()
    {
        _file.Text = null;
        var e = await Assert.ThrowsAsync<ControlException>(() => _client.StatusAsync(Ct));
        Assert.True(e.NotRunning);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task ConnectionRefused_IsNotRunning()
    {
        // A file left behind by a crashed session: a port nothing listens on.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _file.Text = $$"""{"version": 1, "base_url": "http://127.0.0.1:{{port}}", "token": "{{Token}}"}""";
        var e = await Assert.ThrowsAsync<ControlException>(() => _client.StatusAsync(Ct));
        Assert.True(e.NotRunning);
        Assert.DoesNotContain(Token, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirect_IsNotFollowed()
    {
        _server.Handler = (_, _, _) => (307, "{}");
        var e = await Assert.ThrowsAsync<ControlException>(() => _client.StatusAsync(Ct));
        Assert.Equal(307, e.Status);
        Assert.Single(_server.Requests);
    }

    [Fact]
    public async Task NonObjectResponse_IsInvalid()
    {
        _server.Handler = (_, _, _) => (200, "[1, 2]");
        var e = await Assert.ThrowsAsync<ControlException>(() => _client.StatusAsync(Ct));
        Assert.Equal("invalid JSON response", e.Message);
    }

    private sealed class CallbackControlFile(Func<string?> read) : IControlFileSource
    {
        public Task<string?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(read());
    }
}
