using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using WhatDidIDo.Tray.Core.App;
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
    private PopoverView? _view;
    private bool _syncingSlider;
    private bool _sliderDragging;
    private readonly DispatcherTimer _keyCommit = new() { Interval = TimeSpan.FromMilliseconds(600) };

    /// <summary>A start, pause, resume or stop from the primary button or "Stop now".</summary>
    public event EventHandler<string>? CommandRequested;

    /// <summary>The footer's stop confirmation was asked for (true) or cancelled (false).</summary>
    public event EventHandler<bool>? StopConfirmRequested;

    /// <summary>The flyout was shown (true) or hidden (false).</summary>
    public event EventHandler<bool>? OpenChanged;

    /// <summary>The Microphone tile asks for this state.</summary>
    public event EventHandler<bool>? MicToggleRequested;

    /// <summary>A source picked in the input picker.</summary>
    public event EventHandler<MicSource>? MicChosen;

    /// <summary>The input picker opens ("input") or closes (null).</summary>
    public event EventHandler<string?>? ExpandRequested;

    /// <summary>A link in the app was clicked (PHI tile, Now card, a Today bar, Open whatdidido).</summary>
    public event EventHandler<AppLink>? LinkRequested;

    /// <summary>The ticket picker opens ("ticket") or closes (null); see also <see cref="ExpandRequested"/>.</summary>
    public event EventHandler<string>? TicketPicked;

    /// <summary>Assign was clicked with this body.</summary>
    public event EventHandler<AssignmentBody>? AssignRequested;

    /// <summary>A Today bar is hovered or focused (its index), or none is (null).</summary>
    public event EventHandler<int?>? HoverBarChanged;

    /// <summary>Resolves an app's executable name to its icon, or null (initials are shown).</summary>
    public Func<string, Task<Microsoft.UI.Xaml.Media.ImageSource?>>? AppIcon { get; set; }

    /// <summary>The slider shows another stop (a drag, or a key step not yet committed).</summary>
    public event EventHandler<int>? IntervalChanging;

    /// <summary>Commit this stop: a drag ended, 600 ms passed after the last key step, or the flyout closed meanwhile.</summary>
    public event EventHandler<int>? IntervalCommitted;

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
        Root.ActualThemeChanged += (_, _) =>
        {
            _shownBars = [];
            ThemeChanged?.Invoke(this, EventArgs.Empty);
        };

        // A pointer drag commits on release; the slider handles the pointer
        // events itself, so listen to handled ones too.
        IntervalSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            _keyCommit.Stop();
            _sliderDragging = true;
        }), handledEventsToo: true);
        IntervalSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => EndDrag()), handledEventsToo: true);
        IntervalSlider.PointerCaptureLost += (_, _) => EndDrag();
        _keyCommit.Tick += (_, _) => CommitInterval();
    }

    private int SliderIndex => (int)Math.Round(IntervalSlider.Value);

    private void EndDrag()
    {
        if (!_sliderDragging)
            return;
        _sliderDragging = false;
        CommitInterval();
    }

    private void CommitInterval()
    {
        _keyCommit.Stop();
        IntervalCommitted?.Invoke(this, SliderIndex);
    }

    private void IntervalSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingSlider || _view is not { Interval.Sensitive: true })
            return;
        IntervalChanging?.Invoke(this, SliderIndex);
        // Keyboard steps commit after a pause; a drag commits on release.
        if (!_sliderDragging)
        {
            _keyCommit.Stop();
            _keyCommit.Start();
        }
    }

    public bool IsOpen { get; private set; }

    /// <summary>The flyout follows Windows' app theme; the Today bars' colors depend on it.</summary>
    public bool IsDark => Root.ActualTheme == ElementTheme.Dark;

    /// <summary>Windows switched between light and dark.</summary>
    public event EventHandler? ThemeChanged;

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
        // A pending interval change is committed, not dropped (POPOVER.md Amendment 2.2).
        if (_keyCommit.IsEnabled || _sliderDragging)
        {
            _sliderDragging = false;
            CommitInterval();
        }
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
        StatusGlyph.Glyph = status.Mark == StatusMark.Info ? "\uE946" : "\uE7E8";
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
            PrimaryGlyph.Glyph = primary.Command == "pause" ? "\uE769" : "\uE768";
            ToolTipService.SetToolTip(PrimaryButton, primary.AccessibleName);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(PrimaryButton, primary.AccessibleName);
        }
        else
        {
            PrimaryButton.Visibility = Visibility.Collapsed;
        }

        _view = view;
        Notices.Children.Clear();
        foreach (var text in view.Notices)
            Notices.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = text });

        RenderTiles(view.Tiles);
        RenderInput(view.Input);
        RenderInterval(view.Interval);
        RenderNow(view.Now);
        RenderToday(view.Today);
        OpenButton.IsEnabled = view.Open.Sensitive;

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

    private sealed record TileParts(ButtonBase Button, FontIcon Glyph, TextBlock Title, TextBlock Subtitle);

    private TileParts? _micTile, _phiTile, _windowTile;
    private IReadOnlyList<InputItem> _shownSources = [];

    // A Quick Settings tile: glyph, then title over subtitle. Toggle tiles take
    // the accent fill when checked from the toggle button's own style.
    private static TileParts MakeTile(ButtonBase button)
    {
        var glyph = new FontIcon { FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis };
        // The caption style wraps; a tile keeps one line and ellipsizes (the tooltip has the rest).
        var subtitle = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(subtitle);
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(glyph);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        button.Content = grid;
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Padding = new Thickness(12, 8, 12, 8);
        button.MinHeight = 56;
        button.CornerRadius = new CornerRadius(8);
        return new TileParts(button, glyph, title, subtitle);
    }

    private void RenderTiles(Tiles tiles)
    {
        if (_micTile is null)
        {
            _micTile = MakeTile(new ToggleButton());
            _micTile.Button.Click += (_, _) =>
            {
                var on = _view?.Tiles.Mic.On == true;
                // Show the session's truth until it changes; the controller decides.
                ((ToggleButton)_micTile.Button).IsChecked = on;
                MicToggleRequested?.Invoke(this, !on);
            };
            _phiTile = MakeTile(new Button());
            _phiTile.Button.Click += (_, _) =>
            {
                if (_view is { } v)
                    LinkRequested?.Invoke(this, v.PhiLink);
            };
            _phiTile.Subtitle.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
            // Reports only (TRAY.md T8): looks like a tile, takes no input.
            _windowTile = MakeTile(new ToggleButton { IsHitTestVisible = false, IsTabStop = false });
        }

        TilesGrid.Visibility = Show(tiles.Visible);
        TilesGrid.Children.Clear();
        TilesGrid.RowDefinitions.Clear();
        var shown = new List<(TileParts Parts, Tile Tile)> { (_micTile, tiles.Mic), (_phiTile!, tiles.Phi), (_windowTile!, tiles.Window) }
            .Where(t => t.Tile.Visible).ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            var (parts, tile) = shown[i];
            parts.Glyph.Glyph = tile.Glyph;
            parts.Title.Text = tile.Title;
            parts.Subtitle.Text = tile.Subtitle;
            parts.Button.IsEnabled = tile.Sensitive || !tile.Interactive;
            parts.Button.Opacity = tile.Sensitive ? 1 : 0.6;
            if (parts.Button is ToggleButton toggle)
                toggle.IsChecked = tile.On;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(parts.Button, tile.AccessibleName);
            ToolTipService.SetToolTip(parts.Button, tile.Subtitle);
            if (i % 2 == 0)
                TilesGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(parts.Button, i / 2);
            Grid.SetColumn(parts.Button, i % 2);
            TilesGrid.Children.Add(parts.Button);
        }
    }

    private void RenderInput(InputBlock input)
    {
        InputBlock.Visibility = Show(input.Visible);
        InputValue.Text = input.Value;
        InputChevron.Glyph = input.Expanded ? "\uE70E" : "\uE70D";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(InputButton, input.AccessibleName);
        InputList.Visibility = Show(input.Expanded);
        InputEmpty.Text = input.EmptyText ?? "";
        InputEmpty.Visibility = Show(input.EmptyText is not null);

        // Rebuild the radio buttons only when the sources change, so focus stays put.
        if (!input.Items.SequenceEqual(_shownSources))
        {
            _shownSources = input.Items;
            while (InputList.Children.Count > 1)
                InputList.Children.RemoveAt(1);
            foreach (var item in input.Items)
            {
                var radio = new RadioButton { Content = item.Label, IsChecked = item.Checked, GroupName = "wdi-input" };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(radio, item.Label);
                var source = item.Source;
                // Checked, not Click: a screen reader's select (UIA SelectionItem) checks without clicking.
                radio.Checked += (_, _) => MicChosen?.Invoke(this, source);
                InputList.Children.Add(radio);
            }
        }
    }

    private void RenderInterval(IntervalBlock interval)
    {
        IntervalBlock.Visibility = Show(interval.Visible);
        IntervalValue.Text = interval.Value;
        IntervalSlider.IsEnabled = interval.Sensitive;
        if (!_sliderDragging && SliderIndex != interval.Index)
        {
            _syncingSlider = true;
            IntervalSlider.Value = interval.Index;
            _syncingSlider = false;
        }
    }

    private IReadOnlyList<TicketItem> _shownTickets = [];
    private IReadOnlyList<TodayBar> _shownBars = [];
    private string? _iconFor;

    private void RenderNow(NowBlock now)
    {
        NowCard.Visibility = Show(now.Visible);
        NowCard.Opacity = now.Dimmed ? 0.6 : 1;
        if (!now.Visible)
            return;

        NowFocusButton.Visibility = Show(now.Focus is not null);
        if (now.Focus is { } focus)
        {
            NowAppName.Text = focus.AppName;
            NowTitle.Text = focus.Title;
            NowInitials.Text = focus.Initials;
            NowFocusButton.IsEnabled = focus.Sensitive;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(NowFocusButton, focus.AccessibleName);
            ToolTipService.SetToolTip(NowFocusButton, focus.Title.Length > 0 ? focus.Title : null);
            _ = ShowAppIcon(focus.AppId);
        }

        NowStretchBlock.Visibility = Show(now.Stretch is not null);
        if (now.Stretch is { } stretch)
        {
            NowStretchText.Text = stretch.Text;
            TicketLabel.Text = stretch.TicketLabel;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TicketButton, stretch.TicketAccessibleName);
            TicketChevron.Glyph = stretch.TicketExpanded ? "\uE70E" : "\uE70D";
            TicketButton.IsEnabled = stretch.TicketSensitive;
            AssignButton.IsEnabled = stretch.AssignSensitive;
            AssignButton.Content = stretch.AssignBusy ? "Assigning…" : "Assign";
            AssignButton.Tag = stretch.Assign;
            TicketList.Visibility = Show(stretch.TicketExpanded);
            if (!stretch.Tickets.SequenceEqual(_shownTickets))
            {
                _shownTickets = stretch.Tickets;
                TicketList.Children.Clear();
                foreach (var item in stretch.Tickets)
                {
                    var radio = new RadioButton { Content = item.Label, IsChecked = item.Checked, GroupName = "wdi-ticket" };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(radio, item.AccessibleName);
                    var ticket = item.Ticket;
                    radio.Checked += (_, _) =>
                    {
                        TicketPicked?.Invoke(this, ticket);
                        // The list closes under the focused radio: hand focus to the next step, Assign.
                        AssignButton.Focus(FocusState.Keyboard);
                    };
                    TicketList.Children.Add(radio);
                }
            }
        }

        NowLast.Text = now.Last ?? "";
        NowLast.Visibility = Show(now.Last is not null);
        NowError.Text = now.Error ?? "";
        NowError.Visibility = Show(now.Error is not null);
    }

    // The app's real icon when a running process has that executable name, else its initials.
    private async Task ShowAppIcon(string appId)
    {
        if (appId == _iconFor)
            return;
        _iconFor = appId;
        var icon = AppIcon is null || appId.Length == 0 ? null : await AppIcon(appId);
        if (_iconFor != appId)
            return;
        NowIcon.Source = icon;
        NowIcon.Visibility = Show(icon is not null);
        NowInitialsTile.Visibility = Show(icon is null);
    }

    private void RenderToday(TodayBlock today)
    {
        TodayBlock.Visibility = Show(today.Visible);
        TodayLabel.Text = today.Label;
        // Rebuilt only when the bars change, so hover and focus survive a poll.
        if (today.Bars.SequenceEqual(_shownBars))
            return;
        var focused = _shownBars.Count > 0 && TodayBars.Children.Any(c => c is Control { FocusState: not FocusState.Unfocused });
        var focusIndex = TodayBars.Children.Select((c, i) => (c, i)).FirstOrDefault(x => x.c is Control { FocusState: not FocusState.Unfocused }).i;
        _shownBars = today.Bars;
        TodayBars.Children.Clear();
        TodayBars.ColumnDefinitions.Clear();
        var accent = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        foreach (var bar in today.Bars)
        {
            TodayBars.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Height = Math.Max(3, bar.Height * TodayBars.Height),
                VerticalAlignment = VerticalAlignment.Bottom,
                RadiusX = 2,
                RadiusY = 2,
                Fill = bar.Color is { } hex ? new Microsoft.UI.Xaml.Media.SolidColorBrush(ParseColor(hex)) : accent,
            };
            var button = new Button
            {
                Content = rect,
                Padding = new Thickness(0),
                MinWidth = 0,
                BorderThickness = new Thickness(0),
                Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Bottom,
                IsEnabled = bar.Sensitive,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, bar.AccessibleName);
            var index = bar.Index;
            var link = bar.Link;
            button.Click += (_, _) => LinkRequested?.Invoke(this, link);
            button.PointerEntered += (_, _) => HoverBarChanged?.Invoke(this, index);
            button.PointerExited += (_, _) => HoverBarChanged?.Invoke(this, null);
            button.GotFocus += (_, _) => HoverBarChanged?.Invoke(this, index);
            button.LostFocus += (_, _) => HoverBarChanged?.Invoke(this, null);
            Grid.SetColumn(button, bar.Index);
            TodayBars.Children.Add(button);
        }
        // A bar that had focus hands it to the bar at the same index, or the last (POPOVER.md Amendment 2.5).
        if (focused && TodayBars.Children.Count > 0)
            ((Control)TodayBars.Children[Math.Min(focusIndex, TodayBars.Children.Count - 1)]).Focus(FocusState.Keyboard);
    }

    private static Windows.UI.Color ParseColor(string hex)
    {
        var v = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return Windows.UI.Color.FromArgb(0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private void NowFocusButton_Click(object sender, RoutedEventArgs e)
    {
        if (_view?.Now.Focus is { } focus)
            LinkRequested?.Invoke(this, focus.Link);
    }

    private void TicketButton_Click(object sender, RoutedEventArgs e) =>
        ExpandRequested?.Invoke(this, _view?.Now.Stretch?.TicketExpanded == true ? null : "ticket");

    private void AssignButton_Click(object sender, RoutedEventArgs e)
    {
        if (AssignButton.Tag is AssignmentBody body)
            AssignRequested?.Invoke(this, body);
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_view is { } v)
            LinkRequested?.Invoke(this, v.Open.Link);
    }

    private void InputButton_Click(object sender, RoutedEventArgs e) =>
        ExpandRequested?.Invoke(this, _view?.Input.Expanded == true ? null : "input");

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
