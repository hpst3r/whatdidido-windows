using System.Runtime.InteropServices;
using static WhatDidIDo.Tray.Native.NativeMethods;

namespace WhatDidIDo.Tray.Native;

/// <summary>
/// The notification-area icon (TRAY.md T5, section 3): our own
/// <c>Shell_NotifyIcon</c> on a hidden top-level window (a message-only window
/// would miss the <c>TaskbarCreated</c> broadcast after Explorer restarts).
/// Lives on the UI thread, whose loop pumps the window's messages.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = WM_APP + 1;
    private const string ClassName = "whatdidido-windows.tray";

    private static TrayIcon? s_instance;

    private readonly nint _hwnd;
    private readonly uint _taskbarCreated;
    private nint _icon;
    private string _tooltip = "whatdidido";
    private bool _added;

    /// <summary>Left click, or Enter/Space on the focused icon.</summary>
    public event EventHandler? Activated;

    /// <summary>Right click or the context-menu key, at the given screen point (physical pixels).</summary>
    public event EventHandler<POINT>? ContextMenuRequested;

    /// <summary>The taskbar's theme, the DPI, or the shell itself changed: redraw and re-add.</summary>
    public event EventHandler? AppearanceChanged;

    public TrayIcon()
    {
        if (s_instance is not null)
            throw new InvalidOperationException("one tray icon per process");
        s_instance = this;

        var module = GetModuleHandle(null);
        fixed (char* name = ClassName)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = module,
                lpszClassName = name,
            };
            RegisterClassEx(ref wc);
        }
        // WS_POPUP, never shown.
        _hwnd = CreateWindowEx(0, ClassName, "whatdidido tray", 0x80000000, 0, 0, 0, 0, 0, 0, module, 0);
        if (_hwnd == 0)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    }

    public nint Handle => _hwnd;

    public uint Dpi => GetDpiForWindow(_hwnd) is var d and > 0 ? d : 96;

    /// <summary>The small-icon size in physical pixels for the icon's DPI.</summary>
    public int IconSize => GetSystemMetricsForDpi(SM_CXSMICON, Dpi);

    /// <summary>Shows (or replaces) the icon. Takes ownership of <paramref name="icon"/>.</summary>
    public void Update(nint icon, string tooltip)
    {
        var old = _icon;
        _icon = icon;
        _tooltip = tooltip;
        if (!Notify(_added ? NIM_MODIFY : NIM_ADD) && _added)
        {
            // The shell lost it (Explorer restarted before TaskbarCreated arrived).
            _added = false;
            Notify(NIM_ADD);
        }
        if (old != 0 && old != icon)
            DestroyIcon(old);
    }

    /// <summary>The icon's rectangle on screen (physical pixels), or null when the shell can't say.</summary>
    public RECT? Bounds()
    {
        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)sizeof(NOTIFYICONIDENTIFIER), hWnd = _hwnd, uID = IconId };
        return Shell_NotifyIconGetRect(ref id, out var rect) == 0 ? rect : null;
    }

    /// <summary>A Win32 popup menu at <paramref name="at"/>; returns the chosen id, or 0.</summary>
    public int ShowMenu(POINT at, IEnumerable<(int Id, string? Text, bool Enabled)> items)
    {
        var menu = CreatePopupMenu();
        try
        {
            foreach (var (id, text, enabled) in items)
            {
                if (text is null)
                    AppendMenu(menu, MF_SEPARATOR, 0, null);
                else
                    AppendMenu(menu, MF_STRING | (enabled ? 0 : MF_GRAYED), (nuint)id, text);
            }
            // The menu closes on an outside click only if our window is foreground.
            SetForegroundWindow(_hwnd);
            var chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, at.X, at.Y, _hwnd, 0);
            PostMessage(_hwnd, WM_NULL, 0, 0);
            return chosen;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_added)
            Notify(NIM_DELETE);
        _added = false;
        if (_icon != 0)
            DestroyIcon(_icon);
        _icon = 0;
        DestroyWindow(_hwnd);
        s_instance = null;
    }

    private bool Notify(uint message)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        var tip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip;
        tip.AsSpan().CopyTo(new Span<char>(data.szTip, 128));
        var ok = Shell_NotifyIcon(message, ref data);
        if (ok && message == NIM_ADD)
        {
            _added = true;
            data.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref data);
        }
        return ok;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        var self = s_instance;
        if (self is null || hwnd != self._hwnd)
            return DefWindowProc(hwnd, msg, wParam, lParam);
        try
        {
            if (msg == CallbackMessage)
            {
                // NOTIFYICON_VERSION_4: LOWORD(lParam) is the event, wParam the anchor point.
                switch ((uint)(lParam & 0xFFFF))
                {
                    case NIN_SELECT or NIN_KEYSELECT:
                        self.Activated?.Invoke(self, EventArgs.Empty);
                        break;
                    case WM_CONTEXTMENU:
                        self.ContextMenuRequested?.Invoke(self, new POINT { X = (short)(wParam & 0xFFFF), Y = (short)((wParam >> 16) & 0xFFFF) });
                        break;
                }
                return 0;
            }
            if (msg == self._taskbarCreated)
            {
                self._added = false;
                self.AppearanceChanged?.Invoke(self, EventArgs.Empty);
                return 0;
            }
            if (msg is WM_SETTINGCHANGE or WM_DPICHANGED)
                self.AppearanceChanged?.Invoke(self, EventArgs.Empty);
        }
        catch (Exception e)
        {
            // Never let an exception cross the native frame.
            TrayLog.Write($"tray icon: {e.GetType().Name}");
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
