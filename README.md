# whatdidido-windows

Windows 10/11 notification-area control for a
[whatdidido](https://github.com/hpst3r/whatdidido) work session: the Windows
counterpart of [whatdidido-gnome](https://github.com/hpst3r/whatdidido-gnome).
It talks to the work session's local control API (`/control/v1/*`), found
through `%LOCALAPPDATA%\whatdidido\control.json`.

Status: pre-alpha, slice `tray-core` of [docs/TRAY.md](docs/TRAY.md).

- **Tray icon:** a glyph per state (recording in red, paused in amber,
  stopped, starting/stopping, reconnecting, failed, not running), in the
  taskbar's light or dark color, with a microphone badge while the
  microphone is live. The tooltip says the state.
- **Flyout** (left click): the session's state, the round Pause / Resume /
  Start button, notices (a failed action, the session's last error), the
  sample and pending counts, and **Stop session** with a 4 s "Stop now"
  confirmation. Acrylic, rounded, in your accent color, light or dark;
  Escape or a click elsewhere closes it.
- **Menu** (right click): the primary action, Open session page, Quit.

Polling: every 2 s while the flyout is open or the session is busy, else
every 10 s; also when the flyout opens.

Still to come (TRAY.md section 7): microphone, PHI guard and active-window
tiles, the input picker and interval slider, the Now card and Today bars from
the app server, the pause and microphone reminders, settings and start at
login.

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

## Layout

- `src/WhatDidIDo.Tray.Core` (`net10.0`, no WinUI or Win32): every decision.
  `Control/` reads the control file and talks to `/control/v1`;
  `Models/` ports the extension's pure modules (`StatusModel` = `model.js`,
  `PopoverModel` = `popover_model.js`, `PauseReminder` = `pause_reminder.js`)
  with the same strings and rules.
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
