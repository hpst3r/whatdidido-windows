using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using WhatDidIDo.Tray.Core.Models;
using WhatDidIDo.Tray.Native;
using static WhatDidIDo.Tray.Native.NativeMethods;

namespace WhatDidIDo.Tray;

/// <summary>
/// The flyout (TRAY.md T6, section 4): a borderless, always-on-top acrylic
/// window placed by the tray icon like the system flyouts, hidden when it
/// loses activation or on Escape. It renders a <see cref="PopoverView"/> and
/// owns no state beyond widget state.
/// </summary>
public sealed partial class FlyoutWindow : Window
{
    private const int Margin = 12; // epx between the flyout and the taskbar or screen edge

    private readonly nint _hwnd;
    private readonly bool _animations = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
    private RECT _work;
    private RECT? _anchor;
    private double _scale = 1;
    private bool _pulsing;
    private bool _destroying;

    /// <summary>A start, pause, resume or stop from the primary button or "Stop now".</summary>
    public event EventHandler<string>? CommandRequested;

    /// <summary>The footer's stop confirmation was asked for (true) or cancelled (false).</summary>
    public event EventHandler<bool>? StopConfirmRequested;

    /// <summary>The flyout was shown (true) or hidden (false).</summary>
    public event EventHandler<bool>? OpenChanged;

    public FlyoutWindow()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        AppWindow.IsShownInSwitchers = false;
        AppWindow.SetIcon("Assets/whatdidido.ico");
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
            // A border without a caption keeps Windows 11's rounded corners and shadow.
            p.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        }
        var round = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && IsOpen)
                Hide();
        };
        AppWindow.Closing += (_, e) =>
        {
            // Alt+F4 hides; only Quit ends the app.
            if (_destroying)
                return;
            e.Cancel = true;
            Hide();
        };
        Root.SizeChanged += (_, _) => Place();
    }

    public bool IsOpen { get; private set; }

    /// <summary>When the flyout last hid; a tray click right after a hide-on-deactivate is that same click.</summary>
    public long HiddenAt { get; private set; }

    /// <summary>Shows the flyout by the tray icon, or at the cursor's monitor when the icon's place is unknown.</summary>
    internal void ShowAt(RECT? anchor)
    {
        _anchor = anchor;
        POINT at;
        if (anchor is { } a)
            at = new POINT { X = (a.Left + a.Right) / 2, Y = (a.Top + a.Bottom) / 2 };
        else
            GetCursorPos(out at);
        var monitor = MonitorFromPoint(at, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        _work = info.rcWork;
        _scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1;
        if (_anchor is null)
            _anchor = new RECT { Left = at.X, Top = at.Y, Right = at.X, Bottom = at.Y };

        IsOpen = true;
        Place();
        Activate();
        SetForegroundWindow(_hwnd);
        UpdatePulse();
        OpenChanged?.Invoke(this, true);
        PrimaryButton.Focus(FocusState.Programmatic);
    }

    /// <summary>Closes the window for good (Quit).</summary>
    public void Destroy()
    {
        _destroying = true;
        Close();
    }

    public void Hide()
    {
        if (!IsOpen)
            return;
        IsOpen = false;
        HiddenAt = Environment.TickCount64;
        AppWindow.Hide();
        UpdatePulse();
        OpenChanged?.Invoke(this, false);
    }

    /// <summary>Size to the content, then sit on the taskbar's side of the work area next to the icon.</summary>
    private void Place()
    {
        if (!IsOpen || _anchor is not { } icon)
            return;
        var scale = Root.XamlRoot?.RasterizationScale ?? _scale;
        Root.Measure(new Windows.Foundation.Size(Root.Width, double.PositiveInfinity));
        var width = (int)Math.Ceiling(Root.Width * scale);
        var height = (int)Math.Ceiling(Root.DesiredSize.Height * scale);
        var margin = (int)(Margin * scale);
        height = Math.Min(height, _work.Height - 2 * margin);

        var cx = (icon.Left + icon.Right) / 2;
        var cy = (icon.Top + icon.Bottom) / 2;
        int x, y;
        if (cy >= _work.Bottom)        // taskbar at the bottom (the usual case)
            (x, y) = (cx - width / 2, _work.Bottom - margin - height);
        else if (cy < _work.Top)       // top
            (x, y) = (cx - width / 2, _work.Top + margin);
        else if (cx < _work.Left)      // left
            (x, y) = (_work.Left + margin, cy - height / 2);
        else if (cx >= _work.Right)    // right
            (x, y) = (_work.Right - margin - width, cy - height / 2);
        else                           // the overflow area, or no icon: above the point
            (x, y) = (cx - width / 2, cy - margin - height);

        x = Math.Clamp(x, _work.Left + margin, _work.Right - margin - width);
        y = Math.Clamp(y, _work.Top + margin, _work.Bottom - margin - height);
        // Size the client area: the outer size also counts the border.
        AppWindow.ResizeClient(new SizeInt32(width, height));
        var outer = AppWindow.Size;
        AppWindow.Move(new PointInt32(
            Math.Clamp(x - (outer.Width - width) / 2, _work.Left + margin, _work.Right - margin - outer.Width),
            Math.Clamp(y - (outer.Height - height), _work.Top + margin, _work.Bottom - margin - outer.Height)));
    }

    public void Render(PopoverView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var status = view.Status;
        RecordingDot.Visibility = Show(status.Mark == StatusMark.Recording);
        PausedMark.Visibility = Show(status.Mark == StatusMark.Paused);
        IdleDot.Visibility = Show(status.Mark == StatusMark.Idle);
        StatusGlyph.Visibility = Show(status.Mark is StatusMark.Power or StatusMark.Info);
        StatusGlyph.Glyph = status.Mark == StatusMark.Info ? "" : "";
        StatusTitle.Text = status.Title;
        StatusSubtitle.Text = status.Subtitle;
        StatusCard.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            status.Paused ? "SystemFillColorCautionBackgroundBrush" : "CardBackgroundFillColorDefaultBrush"];

        if (status.Primary is { } primary)
        {
            PrimaryButton.Visibility = Visibility.Visible;
            PrimaryButton.Tag = primary.Command;
            PrimaryButton.IsEnabled = primary.Sensitive;
            PrimaryButton.Style = (Style)Application.Current.Resources[primary.Accent ? "AccentButtonStyle" : "DefaultButtonStyle"];
            PrimaryGlyph.Glyph = primary.Command == "pause" ? "" : "";
            ToolTipService.SetToolTip(PrimaryButton, primary.AccessibleName);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PrimaryButton, primary.AccessibleName);
        }
        else
        {
            PrimaryButton.Visibility = Visibility.Collapsed;
        }

        Notices.Children.Clear();
        foreach (var text in view.Notices)
            Notices.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = text });

        var footer = view.Footer;
        FooterBar.Visibility = Show(footer.Visible);
        FooterText.Text = footer.Text;
        StopButton.Visibility = Show(footer.StopVisible);
        StopButton.IsEnabled = footer.StopSensitive;
        CancelStopButton.Visibility = Show(footer.Confirm);
        ConfirmStopButton.Visibility = Show(footer.Confirm);
        ConfirmStopButton.IsEnabled = footer.StopSensitive;

        _pulsing = status.Pulse;
        UpdatePulse();
    }

    private void UpdatePulse()
    {
        if (_pulsing && IsOpen && _animations)
            PulseStoryboard.Begin();
        else
        {
            PulseStoryboard.Stop();
            RecordingDot.Opacity = 1;
        }
    }

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (PrimaryButton.Tag is string command)
            CommandRequested?.Invoke(this, command);
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopConfirmRequested?.Invoke(this, true);
        CancelStopButton.Focus(FocusState.Programmatic);
    }

    private void CancelStopButton_Click(object sender, RoutedEventArgs e) => StopConfirmRequested?.Invoke(this, false);

    private void ConfirmStopButton_Click(object sender, RoutedEventArgs e) => CommandRequested?.Invoke(this, "stop");

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }
}
