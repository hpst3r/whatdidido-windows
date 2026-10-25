using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using WhatDidIDo.Tray.Core.App;
using WhatDidIDo.Tray.Core.Settings;

namespace WhatDidIDo.Tray;

/// <summary>
/// The settings window (TRAY.md section 6), laid out like Windows 11 Settings:
/// the extension's preferences plus start at login. Every change applies at
/// once (the App server URL on Apply or Enter); the window follows changes
/// made elsewhere while it is open (a microphone picked in the flyout).
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly SettingsStore _store;
    private readonly StartAtLogin _startAtLogin;
    private readonly Action<string> _log;
    private bool _syncing;

    internal SettingsWindow(SettingsStore store, StartAtLogin startAtLogin, Action<string> log)
    {
        _store = store;
        _startAtLogin = startAtLogin;
        _log = log;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon("Assets/whatdidido.ico");
        AppWindow.Title = "whatdidido settings";
        Size(760, 820);

        PollActiveBox.Minimum = TraySettings.PollActiveMin;
        PollActiveBox.Maximum = TraySettings.PollActiveMax;
        PollIdleBox.Minimum = TraySettings.PollIdleMin;
        PollIdleBox.Maximum = TraySettings.PollIdleMax;
        PauseReminderBox.Minimum = MicReminderBox.Minimum = 0;
        PauseReminderBox.Maximum = MicReminderBox.Maximum = TraySettings.ReminderMinutesMax;

        _store.Changed += OnStoreChanged;
        Closed += (_, _) => _store.Changed -= OnStoreChanged;
        Activated += (_, e) =>
        {
            // Task Manager may have changed start at login meanwhile.
            if (e.WindowActivationState != WindowActivationState.Deactivated)
                SyncStartAtLogin();
        };
        Sync();
        AppUrlBox.Text = _store.Current.AppUrl;
        SyncAppUrlStatus();
    }

    /// <summary>Sizes the window in effective pixels on its monitor and centers it in the work area.</summary>
    private void Size(int width, int height)
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = Native.NativeMethods.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var w = Math.Min((int)(width * scale), area.Width);
        var h = Math.Min((int)(height * scale), area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    private void OnStoreChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Sync);

    private void Sync()
    {
        _syncing = true;
        var s = _store.Current;
        MicIndicatorSwitch.IsOn = s.ShowMicIndicator;
        Set(PollActiveBox, s.PollActive);
        Set(PollIdleBox, s.PollIdle);
        Set(PauseReminderBox, s.PauseReminderMinutes);
        Set(MicReminderBox, s.MicReminderMinutes);
        var choice = _store.LoadMicChoice();
        ChosenMicText.Text = choice.HasChoice ? (choice.Label.Length > 0 ? choice.Label : "Unnamed microphone") : "None chosen";
        ForgetMicButton.IsEnabled = choice.HasChoice || choice.Label.Length > 0;
        _syncing = false;
        SyncStartAtLogin();
    }

    private static void Set(NumberBox box, int value)
    {
        if (box.Value != value)
            box.Value = value;
    }

    private void SyncStartAtLogin()
    {
        StartAtLoginState state;
        try
        {
            state = _startAtLogin.State();
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _log($"start at login: {e.GetType().Name}");
            return;
        }
        _syncing = true;
        StartAtLoginSwitch.IsOn = state.On;
        _syncing = false;
        StartAtLoginDescription.Text = state switch
        {
            { DisabledInTaskManager: true } => "Turned off in Task Manager's Startup apps; turning it on here enables it again",
            { OtherCommand: true } => "Another copy of the tray starts at login; turning this on starts this one instead",
            _ => "Start the tray when you sign in to Windows",
        };
    }

    private void StartAtLoginSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing)
            return;
        try
        {
            _startAtLogin.Set(StartAtLoginSwitch.IsOn);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _log($"start at login: {ex.GetType().Name}");
        }
        SyncStartAtLogin();
    }

    private void MicIndicatorSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_syncing)
            _store.Update(s => s with { ShowMicIndicator = MicIndicatorSwitch.IsOn });
    }

    private void NumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncing)
            return;
        // A cleared box is NaN: put the current value back rather than guess.
        if (double.IsNaN(args.NewValue))
        {
            Sync();
            return;
        }
        var v = (int)Math.Round(Math.Clamp(args.NewValue, sender.Minimum, sender.Maximum));
        _store.Update(s =>
            sender == PollActiveBox ? s with { PollActive = v }
            : sender == PollIdleBox ? s with { PollIdle = v }
            : sender == PauseReminderBox ? s with { PauseReminderMinutes = v }
            : s with { MicReminderMinutes = v });
        Sync();
    }

    private void ForgetMicButton_Click(object sender, RoutedEventArgs e) => _store.SaveMicChoice(new());

    private void AppUrlApply_Click(object sender, RoutedEventArgs e) => ApplyAppUrl();

    private void AppUrlBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ApplyAppUrl();
            e.Handled = true;
        }
    }

    private void AppUrlBox_TextChanged(object sender, TextChangedEventArgs e) => SyncAppUrlStatus();

    private void ApplyAppUrl()
    {
        // Saved as typed (trimmed), invalid included: the flyout then says the
        // address is not local instead of silently using another one.
        _store.Update(s => s with { AppUrl = AppUrlBox.Text.Trim() });
        SyncAppUrlStatus();
    }

    private void SyncAppUrlStatus()
    {
        var typed = AppUrls.ParseSetting(AppUrlBox.Text);
        var pending = AppUrlBox.Text.Trim() != _store.Current.AppUrl;
        AppUrlApply.IsEnabled = pending;
        AppUrlStatus.Text = typed switch
        {
            { Invalid: true } => "Not a local address: the Now card and Today bars stay hidden. Use http://127.0.0.1:<port>.",
            { Auto: true } => pending ? "Apply to find the app server automatically." : "Found automatically from the work session.",
            _ => pending ? $"Apply to use {typed.Origin}." : $"Using {typed.Origin}.",
        };
    }
}
