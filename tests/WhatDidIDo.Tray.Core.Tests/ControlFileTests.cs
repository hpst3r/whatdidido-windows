using WhatDidIDo.Tray.Core.Control;

namespace WhatDidIDo.Tray.Core.Tests;

public class ControlFileTests
{
    private const string Token = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ";

    [Theory]
    [InlineData("http://127.0.0.1:18801", "http://127.0.0.1:18801")]
    [InlineData("http://127.0.0.1:18801/", "http://127.0.0.1:18801")]
    [InlineData("http://127.0.0.1:1", "http://127.0.0.1:1")]
    [InlineData("http://127.0.0.1:65535", "http://127.0.0.1:65535")]
    public void Parse_AcceptsLoopbackOrigin(string baseUrl, string expected)
    {
        var endpoint = ControlFile.Parse($$"""{"version": 1, "base_url": "{{baseUrl}}", "token": "{{Token}}"}""");
        Assert.Equal(expected, endpoint.BaseUrl);
        Assert.Equal(Token, endpoint.Token);
    }

    [Theory]
    [InlineData("http://localhost:18801")]
    [InlineData("http://[::1]:18801")]
    [InlineData("https://127.0.0.1:18801")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:0")]
    [InlineData("http://127.0.0.1:65536")]
    [InlineData("http://127.0.0.1:018801")]
    [InlineData("http://127.0.0.1:18801/app/")]
    [InlineData("http://127.0.0.1.evil.example:18801")]
    [InlineData("http://user@127.0.0.1:18801")]
    [InlineData(" http://127.0.0.1:18801")]
    public void Parse_RefusesOtherBaseUrls(string baseUrl)
    {
        var e = Assert.Throws<ControlException>(() =>
            ControlFile.Parse($$"""{"version": 1, "base_url": "{{baseUrl}}", "token": "{{Token}}"}"""));
        Assert.Equal("control file base_url is not a loopback http URL", e.Message);
    }

    [Theory]
    [InlineData("not json", "control file is not valid JSON")]
    [InlineData("[]", "control file is not an object")]
    [InlineData("""{"version": 2, "base_url": "http://127.0.0.1:1", "token": "abcdefghijklmnop"}""", "control file has an unsupported version")]
    [InlineData("""{"version": "1", "base_url": "http://127.0.0.1:1", "token": "abcdefghijklmnop"}""", "control file has an unsupported version")]
    [InlineData("""{"version": 1, "base_url": "http://127.0.0.1:1", "token": "short"}""", "control file token is malformed")]
    [InlineData("""{"version": 1, "base_url": "http://127.0.0.1:1", "token": "has space in it ok"}""", "control file token is malformed")]
    [InlineData("""{"version": 1, "base_url": "http://127.0.0.1:1"}""", "control file token is malformed")]
    public void Parse_RefusesMalformedFiles(string text, string message)
    {
        var e = Assert.Throws<ControlException>(() => ControlFile.Parse(text));
        Assert.Equal(message, e.Message);
        Assert.False(e.NotRunning);
    }

    [Fact]
    public void Errors_AndToString_NeverContainTheToken()
    {
        var endpoint = ControlFile.Parse($$"""{"version": 1, "base_url": "http://127.0.0.1:5", "token": "{{Token}}"}""");
        Assert.DoesNotContain(Token, endpoint.ToString(), StringComparison.Ordinal);
        var e = Assert.Throws<ControlException>(() =>
            ControlFile.Parse($$"""{"version": 1, "base_url": "http://evil:5", "token": "{{Token}}"}"""));
        Assert.DoesNotContain(Token, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlainSource_MissingFile_IsNull()
    {
        var source = new PlainControlFileSource(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "control.json"));
        Assert.Null(await source.ReadAsync(TestContext.Current.CancellationToken));
    }
}
