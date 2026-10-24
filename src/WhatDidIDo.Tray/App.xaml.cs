using Microsoft.UI.Xaml;
using WhatDidIDo.Tray.Core.Control;
using WhatDidIDo.Tray.Core.Settings;
using WhatDidIDo.Tray.Native;

namespace WhatDidIDo.Tray;

/// <summary>
/// No main window: the app is the tray icon, the flyout and the poller
/// (<see cref="TrayController"/>). One instance per login session.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Lives as long as the process; Quit disposes both.")]
public partial class App : Application
{
    private Mutex? _single;
    private TrayController? _controller;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"unhandled: {e.Exception.GetType().Name}");
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _single = new Mutex(true, @"Local\whatdidido-windows.tray", out var first);
        if (!first)
        {
            Exit();
            return;
        }
        try
        {
            _ = NativeMethods.SetPreferredAppMode(1); // AllowDark
            NativeMethods.FlushMenuThemes();
        }
        catch (EntryPointNotFoundException)
        {
        }

        var path = ControlFile.DefaultPath();
        IControlFileSource source = path is null ? new MissingControlFile() : new OwnedControlFileSource(path);
        _controller = new TrayController(new ControlClient(source), new SettingsStore(SettingsStore.DefaultPath()), Quit);
        _controller.Start();
    }

    private void Quit()
    {
        _controller?.Dispose();
        _controller = null;
        _single?.ReleaseMutex();
        _single?.Dispose();
        Exit();
    }

    /// <summary>No usable LOCALAPPDATA: the session can't have written a control file either.</summary>
    private sealed class MissingControlFile : IControlFileSource
    {
        public Task<string?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
}
