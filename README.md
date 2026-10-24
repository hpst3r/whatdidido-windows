# whatdidido-windows

Windows 10/11 notification-area control for a
[whatdidido](https://github.com/hpst3r/whatdidido) work session: the Windows
counterpart of [whatdidido-gnome](https://github.com/hpst3r/whatdidido-gnome).
It talks to the work session's local control API (`/control/v1/*`), found
through `%LOCALAPPDATA%\whatdidido\control.json`.

Status: pre-alpha, slices `tray-core`, `tray-popover`, `tray-app` and `tray-reminders` of [docs/TRAY.md](docs/TRAY.md).

- **Tray icon:** a glyph per state (recording in red, paused in amber,
  stopped, starting/stopping, reconnecting, failed, not running), in the
  taskbar's light or dark color, with a microphone badge while the
  microphone is live. The tooltip says the state.
- **Flyout** (left click): the session's state, the round Pause / Resume /
  Start button, notices (a failed action, the session's last error), the
  sample and pending counts, and **Stop session** with a 4 s "Stop now"
  confirmation. Tiles for the **Microphone** (on/off, the microphone in use
  and its trouble), the **PHI guard** count and **Active window** (whether
  the session records windows); the **Input** picker; a **Screenshot
  interval** slider over the app's stops, 5 s to 1 h; from the app server,
  the **Now** card (the focused app and window, and while the time since the
  last entry is unassigned, a ticket picker and **Assign**; else the last
  entry), the **Today** bars (one per 15 minutes, colored by ticket; click
  one to open the Timeline there) and **Open whatdidido**. Acrylic, rounded,
  in your accent color, light or dark; Escape or a click elsewhere closes it.
- **Menu** (right click): the primary action, Open session page, Quit.

Polling: every 2 s while the flyout is open or the session is busy, else
every 10 s; also when the flyout opens.

## Microphone choice

As in whatdidido-gnome (issue #1), the tray never picks a microphone you did
not choose. The choice is saved by its stable id, so a re-enumerated device
is still found. With no saved choice, turning the Microphone tile on sends
nothing: the flyout says "Choose a microphone" and opens the Input list, and
picking one saves it and turns it on. A saved choice that is not connected
leaves the tile off with "<name> is not connected". Picking another source
while the microphone is on switches to it. Polling never turns it on or off.

Settings live in `%LOCALAPPDATA%\whatdidido-windows\settings.json` (the
extension's keys in camelCase; TRAY.md section 6). There is no settings
window yet.

## App server

The Now card, the Today bars and the links come from the whatdidido read
server that serves `/app/` (with `--timesheet`), as in whatdidido-gnome. The
tray finds it from the work session's `/day` redirect (a session started with
`--app-url`), without the control token; `appUrl` in the settings file can
name it instead (`http://127.0.0.1:<port>`, nothing else). The day is read
when the flyout opens and every 15 s while it stays open, never while it is
closed. Requests to it carry no token, cookie, `Origin` or `Sec-Fetch-*`
header and follow no redirects; the server treats them as the local user.

On a Windows read server, start it with `TZ` set to your IANA zone (for
example `America/Los_Angeles`): without `/etc/localtime` it otherwise uses
UTC days (TRAY.md open point c).

## Reminders

As in whatdidido-gnome, as Windows notifications:

- **Recording is paused** ("Paused for N minutes. Screenshots, audio and
  window activity aren't being recorded.") after `pauseReminderMinutes`
  (default 10) paused, and again every that many minutes; **Resume** or
  **Stop reminding** (until the session leaves paused).
- **Check your microphone** ("No speech transcribed from USB headset for N
  minutes.") while recording with the microphone on and the server
  transcribing, when no speech was transcribed for `micReminderMinutes`
  (default 10); **Choose microphone** (opens the flyout's Input list) or
  **Stop reminding** (until speech, another microphone or leaving
  recording).

Each new reminder replaces the last; leaving the state withdraws it; 0 turns
a reminder off. Counting starts at the first poll that saw the state (the
server reports no start time) on the monotonic clock. Do Not Disturb
applies. The tray removes its reminders when it quits.

Windows knows the tray as `whatdidido.windows.tray`, registered per user
under `HKCU\Software\Classes\AppUserModelId` when it starts (that key can
be deleted after removing the tray). Errors go to
`%LOCALAPPDATA%\whatdidido-windows	ray.log`.

Still to come (TRAY.md section 7): a settings window and start at login.

## Build and run

Needs the .NET 10 SDK; no Visual Studio, Windows SDK or Windows App SDK
runtime install (the app is unpackaged and self-contained).

```powershell
dotnet test tests/WhatDidIDo.Tray.Core.Tests
dotnet build src/WhatDidIDo.Tray -p:Platform=x64
src\WhatDidIDo.Tray\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\whatdidido-tray.exe
```

Windows 11 puts a new tray icon in the overflow (`^`); drag it onto the
taskbar to keep it visible. One instance runs per login.

Debug builds take `--show-flyout`, which opens the flyout at start-up, so it
can be checked (for example with UI Automation) without a tray click.

## Layout

- `src/WhatDidIDo.Tray.Core` (`net10.0`, no WinUI or Win32): every decision.
  `Control/` reads the control file and talks to `/control/v1`;
  `Models/` ports the extension's pure modules (`StatusModel` = `model.js`,
  `PopoverModel` = `popover_model.js`, `MicModel`/`MicController` =
  `mic_model.js`, `DayModel` = `day_model.js`, `PauseReminder` =
  `pause_reminder.js`, `MicReminder` = `mic_reminder.js`) with the same
  strings and rules; `App/` finds and
  talks to the app server; `Settings/` reads and writes the settings file.
- `src/WhatDidIDo.Tray` (WinUI 3): `TrayController` polls and renders;
  `FlyoutWindow` draws a `PopoverView`; `Native/` holds the
  `Shell_NotifyIcon` icon, its glyph rendering and the control file's owner
  check.
- `tests/WhatDidIDo.Tray.Core.Tests`: xUnit, including the client against a
  loopback fake of the control API that enforces its request rules.

## Privacy

The control token is read from the session's control file on every request
(and again after a 403), sent only to the `http://127.0.0.1:<port>` in that
file, and never stored, logged or shown. The file and its directory must be
real (no link) and owned by you, your token's default owner, Administrators
or SYSTEM. Requests carry no `Origin`, `Cookie` or `Sec-Fetch-*` headers,
use no proxy and follow no redirects.

## License

AGPL-3.0-or-later. See [LICENSE](LICENSE).
