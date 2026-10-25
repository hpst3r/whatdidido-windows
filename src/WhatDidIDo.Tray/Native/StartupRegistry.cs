using Microsoft.Win32;
using WhatDidIDo.Tray.Core.Settings;

namespace WhatDidIDo.Tray.Native;

/// <summary>
/// The per-user Run and StartupApproved\Run keys (HKCU only; never the
/// machine-wide ones, which need administrator rights).
/// </summary>
internal sealed class StartupRegistry : IStartupRegistry
{
    private const string Base = @"Software\Microsoft\Windows\CurrentVersion";
    private readonly string _run;
    private readonly string _approved;

    public StartupRegistry()
    {
        var root = Base;
#if DEBUG
        // Debug builds: WHATDIDIDO_TRAY_STARTUP_KEY names another HKCU key to use
        // instead, so checks never touch the real start-up entries.
        if (Environment.GetEnvironmentVariable("WHATDIDIDO_TRAY_STARTUP_KEY") is { Length: > 0 } test)
            root = test;
#endif
        _run = $@"{root}\Run";
        _approved = $@"{root}\Explorer\StartupApproved\Run";
    }

    public string? GetRun(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_run);
        return key?.GetValue(name) as string;
    }

    public void SetRun(string name, string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_run);
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public void DeleteRun(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_run, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public byte[]? GetApproved(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_approved);
        return key?.GetValue(name) as byte[];
    }

    public void DeleteApproved(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_approved, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
