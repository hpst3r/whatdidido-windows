# Windows tray control (contract)

Status: draft 2026-10-23; slices `tray-core`, `tray-popover` and `tray-app` written (section 7). Later
changes are numbered amendments at the end.

Goal: the Windows counterpart of the GNOME top-bar extension
([whatdidido-gnome](https://github.com/hpst3r/whatdidido-gnome)): a
notification-area icon showing the work session's state, and a flyout that
looks and behaves like the extension's popover (its `docs/POPOVER.md`), in
the Windows 11 Quick Settings idiom. It talks to the work session's control
API (whatdidido `docs/CONTROL_API.md`) and, for the Now card and Today bars,
to the app server, exactly as the extension does. This is whatdidido
`docs/WINDOWS.md` open point b (slice `win-tray`).

## 1. Decisions

| # | Decision | By |
| --- | --- | --- |
| T1 | **WinUI 3 (Windows App SDK) in C#**, not the Electron shell's tray window: the App UI's popover needs a local read server, which Windows agent mode does not have; WinUI gives real Fluent controls, the user's accent color, light and dark, and native toasts. | user |
| T2 | **Its own repository, `whatdidido-windows`**, like `whatdidido-gnome` (whatdidido `docs/PACKAGE_SPLIT.md` P1: another codebase speaking the protocol). AGPL-3.0-or-later. | user |
| T3 | **.NET 10 (LTS), Windows App SDK 2.5, unpackaged and self-contained** (`WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`): one folder to copy or zip, no MSIX, no runtime installer, no admin rights. Windows 10 1809 (17763) and later. | architect |
| T4 | **Same split as the extension:** `WhatDidIDo.Tray.Core` (`net10.0`, no WinUI or Win32 references) holds every decision: the control-file rules, the HTTP clients, the status and popover view models, the microphone choice, the reminders, the day model. `WhatDidIDo.Tray` (WinUI) only renders those objects and wires Win32. Core is unit-tested with xUnit v3 against a loopback fake of the control API, and runs on Linux too. | architect |
| T5 | **The notification-area icon is our own `Shell_NotifyIcon` P/Invoke**, not H.NotifyIcon (its WinUI package still targets Windows App SDK 1.6). About 200 lines; it also gives the icon's rectangle (`Shell_NotifyIconGetRect`) to place the flyout. | architect |
| T6 | **The flyout is a borderless, always-on-top WinUI window** with the `DesktopAcrylic` backdrop, rounded corners (DWM), no taskbar or Alt+Tab entry, placed above (or beside) the icon inside the work area like the system flyouts, and hidden when it loses activation or on Escape. Width 360 epx. | architect |
| T7 | **Tray icons are drawn at runtime from Segoe Fluent Icons** (Segoe MDL2 Assets on Windows 10), white or black to match the taskbar's theme (`SystemUsesLightTheme`), at the icon's DPI. Each state keeps its own shape as on GNOME; red (recording) and amber (paused) are added cues, never the only one. | architect |
| T8 | **No window reporting.** On Windows the session reads the focused window itself (whatdidido `docs/WINDOWS.md` W8); the tray never posts `/control/v1/window`. The Active window tile shows whether the session records windows (`window_activity.available`) and cannot toggle it (open point a). | architect |
| T9 | **Settings are a JSON file**, `%LOCALAPPDATA%\whatdidido-windows\settings.json`, with the extension's keys (section 6) in camelCase. It never holds the control token. | architect |

## 2. Control file and requests

1. The control file is `%LOCALAPPDATA%\whatdidido\control.json`, written by
   the session (whatdidido `docs/WINDOWS.md` section 4). It is read on every
   request and again after a `403`, as the extension does.
2. Accepted exactly as the extension's `parseControlFile`: `version` 1;
   `base_url` `http://127.0.0.1:<port>` (port 1..65535, optional trailing
   `/`; `[::1]` is refused, as for the app server since POPOVER.md
   Amendment 1); `token` matching `^[A-Za-z0-9_-]{16,512}$`. Error texts
   never quote the file.
3. In addition, the file and its directory must be real (no link or
   junction) and owned by the current user, the owner the tray's token gives
   new objects, `Administrators` or `SYSTEM`. An elevated session writes them
   owned by `Administrators` (its token's default owner), which the tray's own
   token need not share; no unprivileged account can make either of the last
   two the owner. Any other owner is a poll error ("Can't read the capture
   status"), so another account's planted file never receives a request.
4. No file, or a refused connection: **not running** (nothing logged).
5. Requests: `HttpClient` with no proxy, no cookies, no automatic redirects,
   a 10 s timeout, `Authorization: Bearer <token>`, `User-Agent:
   whatdidido-windows/<version>`, and never `Origin`, `Cookie` or
   `Sec-Fetch-*`. POST bodies are `application/json` with exactly the
   documented keys.
6. Errors are `ControlException {Status, Code, NotRunning}` with the extension's
   texts (`model.js` `errorText`). The token never appears in an error, a
   log line or the UI.

## 3. Tray icon

1. States and glyphs (Segoe Fluent Icons code points): recording `E7C8`
   (record, red), paused `E769` (pause, amber), stopped `E71A` (stop),
   starting and stopping `F16A` (progress ring), recovering `E72C`
   (refresh), failed `E783` (error), not running `F384` (disconnected).
2. While the microphone is live (on, and `mic.state` not `limit_reached` or
   `failed`) and `showMicIndicator` is on, a small microphone badge is drawn
   in the icon's bottom-right corner (`E720`). Windows has one icon per app,
   so the badge replaces the GNOME second icon.
3. The tooltip is the extension's accessible name: `whatdidido: Recording,
   microphone on`.
4. Left click or Enter toggles the flyout. Right click opens a small context
   menu: the primary action (Start, Pause or Resume), Open session page,
   Settings (with `tray-settings`), Quit.
5. After Explorer restarts (`TaskbarCreated`) the icon is added again.

## 4. Flyout

Blocks, strings, rules and order are those of the extension's popover
(POPOVER.md section 4 and its amendments), ported from `popover_model.js`
into `PopoverModel` with the same names. Windows rendering:

| Block | WinUI |
| --- | --- |
| Header | the app icon, "whatdidido", a Settings `Button` (glyph `E713`) |
| Status card | a `CardBackgroundFillColorDefault` card, 8 px radius; the primary action is a round 40 px `Button`, accent-styled when it resumes |
| Notices | an `InfoBar` (Warning severity) per notice, not closable |
| Tiles | Quick Settings tiles, 2 per row in the extension's order (Microphone, PHI guard, Active window), glyph beside title and one-line subtitle: Microphone a `ToggleButton` (accent fill when on), PHI guard a `Button` with an amber subtitle, Active window a `ToggleButton` that takes no input (T8). Insensitive tiles are dimmed. |
| Input | a row button, "Input" and the active microphone (else the saved choice, else "Choose…") with a chevron; open, a `RadioButton` per source below it, the saved choice checked. Rebuilt only when the sources change, so polling keeps focus. |
| Screenshot interval | label and value on a card, a `Slider` over the 14 stops with inline ticks; a pointer drag commits once on release, a click on the track at once, arrow keys 600 ms after the last step; closing the flyout commits a pending step (POPOVER.md Amendment 2.2); a saved stop is held for 3 s until a status reports it (Amendment 2.3) |
| Now | a card: "NOW", the focused app and title as a button (opens the Timesheet); the app's icon from a running process with that executable name (the session reports `app_id` as the file name, `Code.exe`), else its initials on an accent square; the unassigned stretch with a ticket picker (a row button opening `RadioButton`s, "<ticket> · <client>") and an accent Assign `Button`, or the last entry; dimmed unless recording |
| Today | "Today" and the range (the hovered or focused bar's time and ticket instead), a row of bar `Button`s colored from the app's `win` skin palette (ticket colors, `--warn` paused, `--faint` other states, the system accent for active and unassigned); a click opens the Timeline at that time. Rebuilt only when the bars change; a focused bar hands focus to the same index (Amendment 2.5) |
| Open whatdidido | an accent `Button`, full width |
| Footer | samples and pending; Stop session as a text button in the system critical color, turning into Cancel and Stop now for 4 s |

Colors are theme resources (`SystemAccentColor`, `SystemFillColorCritical`,
`SystemFillColorCaution`), so light, dark, high contrast and the user's
accent all follow Windows. Times follow the user's short time format
(`CultureInfo.CurrentCulture`, so 12 or 24 hours as in Windows settings).
Keyboard: Tab and arrow keys move, Space and Enter activate, Escape closes.

Polling as the extension: every 2 s while the flyout is open or the state is
starting, recording, recovering or stopping, else every 10 s, and at once
when the flyout opens. The day is read when the flyout opens, every 15 s
while it is open, and after an Assign; never while it is closed.

### 4.1 Windows differences in `tray-popover`

- The flyout's notice (a failed action) clears when the flyout closes.
- Picking a ticket moves keyboard focus to Assign (the list closes under the
  focused radio button); the input and ticket lists react to `Checked`, so
  a screen reader's UI Automation select works like a click.
- The Now card's app icon needs the app to be running as this user
  (`Process.MainModule`); other apps show initials, as on GNOME without a
  `.desktop` match.
- The mic badge on the tray icon is busy at 16 px (100 % scale); a cleaner
  composition is later polish.

## 5. Reminders

The pause reminder and the microphone reminder follow the extension
(`pause_reminder.js`, `mic_reminder.js`; README "Pause reminder" and
"Microphone reminder"), on the monotonic clock (`Stopwatch`). They are app
notifications (`AppNotificationManager`) with the buttons **Resume** /
**Stop reminding** and **Choose microphone** / **Stop reminding**; each new
one replaces the last (same tag), and leaving the state removes it.

## 6. Settings

The extension's keys (README "Settings"), in camelCase: `pollActive` (2),
`pollIdle` (10), `microphoneStableId`, `microphoneId`, `microphoneLabel`,
`showMicIndicator` (true), `pauseReminderMinutes` (10), `micReminderMinutes`
(10), `appUrl` (empty). Not ported: `report-active-window` and
`window-title-excluded-apps` (T8). Plus `startAtLogin` (false), written as
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\whatdidido-windows`
only when the user turns it on in Settings.

## 7. Slices

| Slice | Content | Done when |
| --- | --- | --- |
| `tray-core` (written) | Solution, Core: control file, `ControlClient`, `StatusModel` (`model.js`), tests against a loopback fake. App: tray icon per state with tooltip, flyout with the status card, notices, footer and Stop confirmation. | the icon follows a live session through Start, Pause, Resume and Stop |
| `tray-popover` (written) | `MicModel` and `MicController` (`mic_model.js`, issue #1 rules), `ControlClient.SetMicOnAsync` and the typed inventory, `PopoverModel` tiles, input picker and interval, `SettingsStore` (section 6). App: the tiles, the input picker, the slider. | done live against a stateful fake of `/control/v1` (Windows sessions have no audio yet): the issue #1 prompt, a pick that turns the mic on, a switch while live, off and on with the saved choice, a disconnected choice that sends nothing; slider by drag, click and keys |
| `tray-app` (written) | `AppClient` and `AppUrls` (POPOVER.md section 3 and Amendments 1 and 2: discovery from the session's `/day` redirect, the `appUrl` override, no redirects, the error envelope), `TimesheetDay`, `DayModel` (day.ts, palette.ts with the `win` skin's colors), `PopoverModel` Now, Today, Open and the links; request bodies written without reflection (`JsonBody`). App: the Now card with Assign, the Today bars, Open whatdidido, the PHI tile's link. | done live against a real read server with `--timesheet` (discovery through a real session's `/day`, the day, an Assign the server stored, a refused body); the flyout driven headless with UI Automation against a fake app server (Now card, bars, ticket pick, Assign, a `409 conflict`) |
| `tray-reminders` | Both reminders as app notifications. | |
| `tray-settings` | Settings window, start at login. | |
| `tray-ci` | A `windows-latest` workflow: build, Core tests, a zipped self-contained folder as the artifact. | |

## 8. Open points

- **a. Window activity on Windows.** The GNOME extension's "Record active
  window" switch and per-app title exclusion have no Windows equivalent:
  the session's `--window-activity` is a launch flag. A runtime toggle (or
  an exclusion list) needs a control API amendment in whatdidido.
- **b. Starting the session.** "Capture not running" could offer to start
  the work session once whatdidido `docs/WINDOWS.md` open point c (a
  scheduled task at logon) exists.
- **c. The read server on Windows** (whatdidido, found in `tray-app`'s live
  check). Its day zone comes from `--day-summaries-timezone`, `TZ` or
  `/etc/localtime`, else UTC; Windows has no `/etc/localtime`, so without
  `TZ` its days are UTC days and the tray's "today" (this machine's date)
  is the wrong one in the evening. And `--app-ui` fails at startup
  (`os.O_NOFOLLOW` in `web/app_ui.py`), so Open whatdidido and the links
  have no `/app/` to open on a Windows read server yet. Both belong to
  whatdidido's `win-embedded` portability work.
- **d. Opening links** (`Process.Start` with the shell) was not exercised
  live; it is the standard URL launch.
