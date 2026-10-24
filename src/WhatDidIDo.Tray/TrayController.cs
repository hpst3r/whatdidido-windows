using System.Diagnostics;
using Microsoft.UI.Dispatching;
using WhatDidIDo.Tray.Core.Control;
using WhatDidIDo.Tray.Core.Models;
using WhatDidIDo.Tray.Native;

namespace WhatDidIDo.Tray;

/// <summary>
/// Polls the work session and keeps the tray icon and the flyout in step, as
/// the extension's indicator does (TRAY.md section 4, "Polling"). Everything
/// runs on the UI thread.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private const int MenuPrimary = 1, MenuOpenSession = 2, MenuQuit = 9;
    private static readonly TimeSpan StopConfirmFor = TimeSpan.FromSeconds(4);

    private readonly ControlClient _client;
    private readonly Action _quit;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _pollTimer;
    private readonly DispatcherQueueTimer _stopConfirmTimer;
    private readonly TrayIcon _tray = new();
    private readonly FlyoutWindow _flyout = new();
    private readonly PauseReminder _pause = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly HashSet<string> _logged = [];
    private readonly CancellationTokenSource _cts = new();

    private SessionStatus? _status;
    private bool _running;
    private string? _pollError;
    private string? _notice;
    private bool _commandBusy;
    private bool _stopConfirm;
    private bool _polling;
    private bool _pollAgain;
    private TrayView? _shownTray;
    private bool? _shownLight;
    private int _shownSize;

    public TrayController(ControlClient client, Action quit)
    {
        _client = client;
        _quit = quit;
        _pollTimer = _dispatcher.CreateTimer();
        _pollTimer.IsRepeating = false;
        _pollTimer.Tick += (_, _) => PollNow();
        _stopConfirmTimer = _dispatcher.CreateTimer();
        _stopConfirmTimer.IsRepeating = false;
        _stopConfirmTimer.Interval = StopConfirmFor;
        _stopConfirmTimer.Tick += (_, _) => SetStopConfirm(false);

        _tray.Activated += (_, _) => ToggleFlyout();
        _tray.ContextMenuRequested += (_, at) => ShowMenu(at);
        _tray.AppearanceChanged += (_, _) =>
        {
            _shownTray = null;
            Render();
        };
        _flyout.CommandRequested += (_, command) => _ = RunCommand(command);
        _flyout.StopConfirmRequested += (_, on) => SetStopConfirm(on);
        _flyout.OpenChanged += (_, open) =>
        {
            if (open)
                PollNow();
            else
            {
                SetStopConfirm(false);
                Schedule();
            }
        };
    }

    public void Start()
    {
        Render();
        PollNow();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _pollTimer.Stop();
        _stopConfirmTimer.Stop();
        _tray.Dispose();
        _flyout.Destroy();
        _client.Dispose();
        _cts.Dispose();
    }

    private long Now => _clock.ElapsedMilliseconds;

    private void ToggleFlyout()
    {
        if (_flyout.IsOpen)
            _flyout.Hide();
        // The click that deactivated (and so hid) the flyout arrives here next: don't reopen it.
        else if (Environment.TickCount64 - _flyout.HiddenAt > 300)
            _flyout.ShowAt(_tray.Bounds());
    }

    private void ShowMenu(Native.POINT at)
    {
        var allowed = StatusModel.Allowed(_running ? _status?.State : null);
        var primary = allowed.Pause ? "pause" : allowed.Resume ? "resume" : allowed.Start ? "start" : null;
        var label = primary switch
        {
            "pause" => "Pause recording",
            "resume" => "Resume recording",
            _ => "Start recording",
        };
        var chosen = _tray.ShowMenu(at,
        [
            (MenuPrimary, label, primary is not null && !_commandBusy),
            (MenuOpenSession, "Open session page", _running),
            (0, null, false),
            (MenuQuit, "Quit whatdidido tray", true),
        ]);
        switch (chosen)
        {
            case MenuPrimary when primary is not null:
                _ = RunCommand(primary);
                break;
            case MenuOpenSession:
                _ = OpenSessionPage();
                break;
            case MenuQuit:
                _quit();
                break;
        }
    }

    private async Task OpenSessionPage()
    {
        try
        {
            var endpoint = await _client.EndpointAsync(_cts.Token);
            Process.Start(new ProcessStartInfo(endpoint.BaseUrl + "/session") { UseShellExecute = true });
        }
        catch (ControlException e)
        {
            _notice = StatusModel.ErrorText(e);
            Render();
        }
    }

    private void SetStopConfirm(bool on)
    {
        _stopConfirm = on;
        _stopConfirmTimer.Stop();
        if (on)
            _stopConfirmTimer.Start();
        Render();
    }

    private async Task RunCommand(string command)
    {
        if (_commandBusy)
            return;
        _commandBusy = true;
        _notice = null;
        Render();
        try
        {
            await _client.CommandAsync(command, _cts.Token);
        }
        catch (ControlException e)
        {
            _notice = StatusModel.ErrorText(e);
            LogOnce($"command: {e.Message}");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _commandBusy = false;
            if (command == "stop")
                _stopConfirm = false;
        }
        PollNow();
    }

    /// <summary>Polls at once; a request while a poll runs polls again right after it.</summary>
    private void PollNow()
    {
        _pollTimer.Stop();
        if (_polling)
        {
            _pollAgain = true;
            return;
        }
        _ = Poll();
    }

    private async Task Poll()
    {
        _polling = true;
        try
        {
            _status = await _client.StatusAsync(_cts.Token);
            _running = true;
            _pollError = null;
        }
        catch (ControlException e)
        {
            _status = null;
            _running = false;
            // "Not running" is the normal idle case: nothing logged, no error shown.
            _pollError = e.NotRunning ? null : StatusModel.ErrorText(e);
            if (!e.NotRunning)
                LogOnce($"poll: {e.Message}");
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _polling = false;
        }
        _pause.Observe(_running ? _status?.State : null, Now);
        Render();
        if (_pollAgain)
        {
            _pollAgain = false;
            PollNow();
        }
        else
            Schedule();
    }

    private void Schedule()
    {
        if (_polling)
            return;
        _pollTimer.Interval = TimeSpan.FromSeconds(StatusModel.PollPeriod(_running ? _status?.State : null, _flyout.IsOpen));
        _pollTimer.Start();
    }

    private void Render()
    {
        var tray = StatusModel.Tray(_status, _running);
        var light = TrayIconRenderer.TaskbarIsLight();
        var size = _tray.IconSize;
        if (tray != _shownTray || light != _shownLight || size != _shownSize)
        {
            _tray.Update(TrayIconRenderer.Render(tray, size, light), tray.Tooltip);
            (_shownTray, _shownLight, _shownSize) = (tray, light, size);
        }
        _flyout.Render(PopoverModel.View(
            _status,
            _running,
            _pollError,
            _notice,
            _pause.PausedFor(Now),
            new PopoverUi(_commandBusy, _stopConfirm)));
    }

    // Errors are logged once per distinct message, never with the token (TRAY.md 2.6).
    private void LogOnce(string message)
    {
        if (_logged.Count < 32 && _logged.Add(message))
            Debug.WriteLine($"whatdidido-tray: {message}");
    }
}
