using WhatDidIDo.Tray.Core.Settings;

namespace WhatDidIDo.Tray.Core.Tests;

public class StartAtLoginTests
{
    private const string Exe = @"C:\Tools\whatdidido-windows\whatdidido-tray.exe";

    private sealed class FakeRegistry : IStartupRegistry
    {
        public Dictionary<string, string> Run { get; } = [];
        public Dictionary<string, byte[]> Approved { get; } = [];

        public string? GetRun(string name) => Run.GetValueOrDefault(name);
        public void SetRun(string name, string command) => Run[name] = command;
        public void DeleteRun(string name) => Run.Remove(name);
        public byte[]? GetApproved(string name) => Approved.GetValueOrDefault(name);
        public void DeleteApproved(string name) => Approved.Remove(name);
    }

    private static readonly byte[] Enabled = [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] Disabled = [3, 0, 0, 0, 0x9a, 0x2b, 0x1c, 0x3d, 0, 0, 0, 0];

    [Fact]
    public void Off_ByDefault()
    {
        Assert.Equal(new StartAtLoginState(false, false, false), new StartAtLogin(new FakeRegistry(), Exe).State());
    }

    [Fact]
    public void TurningOn_WritesTheQuotedExecutable_AndOffDeletesIt()
    {
        var registry = new FakeRegistry();
        var start = new StartAtLogin(registry, Exe);
        start.Set(true);
        Assert.Equal($"\"{Exe}\"", registry.Run["whatdidido-windows"]);
        Assert.Equal(new StartAtLoginState(true, false, false), start.State());
        start.Set(false);
        Assert.Empty(registry.Run);
        Assert.False(start.State().On);
    }

    [Fact]
    public void TaskManagerDisabled_ShowsAsOff_AndTurningOnClearsTheMark()
    {
        var registry = new FakeRegistry();
        registry.Run["whatdidido-windows"] = $"\"{Exe}\"";
        registry.Approved["whatdidido-windows"] = Disabled;
        var start = new StartAtLogin(registry, Exe);
        Assert.Equal(new StartAtLoginState(false, true, false), start.State());
        start.Set(true);
        Assert.Empty(registry.Approved);
        Assert.True(start.State().On);

        registry.Approved["whatdidido-windows"] = Enabled;
        Assert.True(start.State().On);
        start.Set(true);
        Assert.Equal(Enabled, registry.Approved["whatdidido-windows"]);   // an enabled mark is left alone
        start.Set(false);
        Assert.Empty(registry.Approved);
    }

    [Fact]
    public void AnotherCopy_IsNotUs()
    {
        var registry = new FakeRegistry();
        registry.Run["whatdidido-windows"] = "\"D:\\old\\whatdidido-tray.exe\"";
        var start = new StartAtLogin(registry, Exe);
        Assert.Equal(new StartAtLoginState(false, false, true), start.State());
        start.Set(true);
        Assert.True(start.State().On);
    }

    [Theory]
    [InlineData("\"C:\\Tools\\whatdidido-windows\\whatdidido-tray.exe\"", true)]
    [InlineData("\"c:\\tools\\WHATDIDIDO-WINDOWS\\whatdidido-tray.exe\" --flag", true)]
    [InlineData("C:\\Tools\\whatdidido-windows\\whatdidido-tray.exe", true)]
    [InlineData("  \"C:\\Tools\\whatdidido-windows\\.\\whatdidido-tray.exe\"  ", true)]
    [InlineData("\"C:\\Tools\\other\\whatdidido-tray.exe\"", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsCommandFor(string? command, bool expected) => Assert.Equal(expected, StartAtLogin.IsCommandFor(command, Exe));

    [Fact]
    public void IsDisabled()
    {
        Assert.False(StartAtLogin.IsDisabled(null));
        Assert.False(StartAtLogin.IsDisabled([]));
        Assert.False(StartAtLogin.IsDisabled(Enabled));
        Assert.True(StartAtLogin.IsDisabled(Disabled));
        Assert.True(StartAtLogin.IsDisabled([1, 0]));
        Assert.False(StartAtLogin.IsDisabled([6, 0]));
    }
}

public class TraySettingsRangeTests
{
    [Theory]
    [InlineData(1, 2, 1, 2)]
    [InlineData(60, 3600, 60, 3600)]
    [InlineData(0, 1, 2, 10)]
    [InlineData(61, 3601, 2, 10)]
    public void Ranges_AreTheExtensions(int active, int idle, int expectActive, int expectIdle)
    {
        var s = new TraySettings { PollActive = active, PollIdle = idle }.Normalized();
        Assert.Equal((expectActive, expectIdle), (s.PollActive, s.PollIdle));
    }
}
