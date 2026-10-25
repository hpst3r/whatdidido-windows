namespace WhatDidIDo.Tray.Core.Settings;

/// <summary>
/// The two per-user registry values start at login reads and writes, behind
/// an interface so the rules are tested without a registry: the
/// <c>Run</c> value (the command Windows starts at sign-in) and Task
/// Manager's <c>StartupApproved\Run</c> value (enabled or disabled).
/// </summary>
public interface IStartupRegistry
{
    string? GetRun(string name);

    void SetRun(string name, string command);

    void DeleteRun(string name);

    byte[]? GetApproved(string name);

    void DeleteApproved(string name);
}

/// <summary>Start at login as the settings window shows it.</summary>
/// <param name="On">The tray starts at sign-in.</param>
/// <param name="DisabledInTaskManager">Our Run value is there but Task Manager's Startup apps turned it off.</param>
/// <param name="OtherCommand">The Run value starts another copy of the tray (a different path).</param>
public sealed record StartAtLoginState(bool On, bool DisabledInTaskManager, bool OtherCommand);

/// <summary>
/// Start at login (TRAY.md section 6): the value
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run\whatdidido-windows</c>,
/// written only when the user turns it on and deleted when they turn it off.
/// The registry, not the settings file, is the state, so a change in Task
/// Manager shows here.
/// </summary>
public sealed class StartAtLogin(IStartupRegistry registry, string executablePath)
{
    public const string ValueName = "whatdidido-windows";

    /// <summary>The Run command: the executable, quoted.</summary>
    public static string Command(string executablePath) => $"\"{executablePath}\"";

    /// <summary>Whether a Run command starts this executable (quotes and case aside).</summary>
    public static bool IsCommandFor(string? command, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;
        var c = command.Trim();
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            c = end > 0 ? c[1..end] : c.Trim('"');
        }
        return string.Equals(Path.GetFullPath(c), Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Task Manager's StartupApproved value: its first byte is even when
    /// enabled (02) and odd when disabled (03, or 01 for some entries);
    /// missing means enabled.
    /// </summary>
    public static bool IsDisabled(byte[]? approved) => approved is { Length: > 0 } && (approved[0] & 1) == 1;

    public StartAtLoginState State()
    {
        var command = registry.GetRun(ValueName);
        if (command is null)
            return new StartAtLoginState(false, false, false);
        var ours = IsCommandFor(command, executablePath);
        var disabled = IsDisabled(registry.GetApproved(ValueName));
        return new StartAtLoginState(ours && !disabled, ours && disabled, !ours);
    }

    /// <summary>
    /// On: (re)write the Run value for this executable and clear a Task
    /// Manager "disabled" mark, since the user asked here. Off: delete the Run
    /// value (and the mark, which would otherwise outlive it).
    /// </summary>
    public void Set(bool on)
    {
        if (on)
        {
            registry.SetRun(ValueName, Command(executablePath));
            if (IsDisabled(registry.GetApproved(ValueName)))
                registry.DeleteApproved(ValueName);
        }
        else
        {
            registry.DeleteRun(ValueName);
            registry.DeleteApproved(ValueName);
        }
    }
}
