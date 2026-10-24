using System.Runtime.InteropServices;
using System.Security;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Windows.UI.Notifications;
using WhatDidIDo.Tray.Core.Models;

namespace WhatDidIDo.Tray.Native;

/// <summary>
/// The reminders as Windows toast notifications (TRAY.md section 5). A new
/// reminder replaces the shown one of its kind (same tag), a withdrawn one is
/// removed, and a click comes back as <see cref="Invoked"/> or
/// <see cref="Opened"/> on the UI thread through the toast's own
/// <c>Activated</c> event while the tray runs.
/// </summary>
/// <remarks>
/// This is the plain <c>Windows.UI.Notifications</c> API, not the Windows App
/// SDK's <c>AppNotificationManager</c>: that one needs the Windows App
/// Runtime's Singleton package, which a self-contained, installer-free tray
/// (T3) does not have. An unpackaged app names itself to the notification
/// platform with an AppUserModelID registered under
/// <c>HKCU\Software\Classes\AppUserModelId</c> (display name and icon).
/// </remarks>
internal sealed partial class Notifications : IDisposable
{
    public const string AppId = "whatdidido.windows.tray";

    private readonly DispatcherQueue _dispatcher;
    private readonly Action<string> _log;
    private readonly ToastNotifier? _notifier;
    // The shown toasts, kept so their Activated events stay subscribed.
    private readonly Dictionary<ReminderKind, ToastNotification> _shown = [];

    /// <summary>A button: the reminder and what it asks for.</summary>
    public event EventHandler<(ReminderKind Kind, ReminderAction Action)>? Invoked;

    /// <summary>The body of a reminder was clicked.</summary>
    public event EventHandler<ReminderKind>? Opened;

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    public Notifications(DispatcherQueue dispatcher, Action<string> log)
    {
        _dispatcher = dispatcher;
        _log = log;
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}"))
            {
                key.SetValue("DisplayName", "whatdidido");
                key.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "Assets", "whatdidido.png"));
            }
            _ = SetCurrentProcessExplicitAppUserModelID(AppId);
            _notifier = ToastNotificationManager.CreateToastNotifier(AppId);
            try
            {
                // Turned off in Settings, or by policy: log it, show anyway (Windows drops them).
                if (_notifier.Setting != NotificationSetting.Enabled)
                    log($"notifications: {_notifier.Setting}");
            }
            catch (COMException e)
            {
                // An app known only by its registry AppUserModelId has no setting to read yet.
                log($"notifications: setting 0x{e.HResult:X8}");
            }
            log("notifications: ready");
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or IOException)
        {
            _notifier = null;
            log($"notifications: {e.GetType().Name} 0x{e.HResult:X8}");
        }
    }

    public bool Available => _notifier is not null;

    // "Element not found": the history holds nothing to remove, which is fine.
    private const int NotFound = unchecked((int)0x80070490);

    /// <summary>Removes reminders left by an earlier run (a crash, a kill): they are stale.</summary>
    public void ClearStale() => RemoveGroup();

    private void RemoveGroup()
    {
        if (_notifier is null)
            return;
        try
        {
            ToastNotificationManager.History.RemoveGroup(ReminderNotice.Group, AppId);
        }
        catch (COMException e) when (e.HResult == NotFound)
        {
        }
        catch (COMException e)
        {
            _log($"notifications: clear 0x{e.HResult:X8}");
        }
    }

    /// <summary>Shows a reminder, replacing the shown one of its kind.</summary>
    public void Show(ReminderNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        if (_notifier is null)
            return;
        var actions = string.Concat(notice.Buttons.Select(b =>
            $"<action content=\"{Escape(b.Label)}\" activationType=\"foreground\" " +
            $"arguments=\"{Escape(ReminderNotice.Encode(ReminderNotice.Arguments(notice.Kind, b.Action)))}\"/>"));
        var xml = $"<toast launch=\"{Escape(ReminderNotice.Encode([new("open", notice.Tag)]))}\">" +
            "<visual><binding template=\"ToastGeneric\">" +
            $"<text>{Escape(notice.Title)}</text><text>{Escape(notice.Body)}</text>" +
            $"</binding></visual><actions>{actions}</actions></toast>";
        var doc = new Windows.Data.Xml.Dom.XmlDocument();
        doc.LoadXml(xml);
        var toast = new ToastNotification(doc)
        {
            Tag = notice.Tag,
            Group = ReminderNotice.Group,
            // High priority: a banner, kept at the top; Do Not Disturb still applies.
            Priority = ToastNotificationPriority.High,
        };
        var kind = notice.Kind;
        toast.Activated += (_, args) => OnActivated(kind, args);
        try
        {
            _notifier.Show(toast);
            _shown[kind] = toast;
            _log($"notifications: showed {notice.Tag}");
        }
        catch (COMException e)
        {
            _log($"notifications: show 0x{e.HResult:X8}");
        }
    }

    /// <summary>Removes the shown reminder of this kind, if any (idempotent).</summary>
    public void Withdraw(ReminderKind kind)
    {
        if (_notifier is null)
            return;
        _shown.Remove(kind);
        try
        {
            ToastNotificationManager.History.Remove(ReminderNotice.TagOf(kind), ReminderNotice.Group, AppId);
        }
        catch (COMException e) when (e.HResult == NotFound)
        {
        }
        catch (COMException e)
        {
            _log($"notifications: remove 0x{e.HResult:X8}");
        }
    }

    public void Dispose()
    {
        if (_notifier is null)
            return;
        _shown.Clear();
        // No reminder outlives the tray: its buttons would have no one to act on them.
        RemoveGroup();
    }

    // Raised on a thread-pool thread by the notification platform.
    private void OnActivated(ReminderKind kind, object args)
    {
        var arguments = ReminderNotice.Decode((args as ToastActivatedEventArgs)?.Arguments);
        if (ReminderNotice.Parse(arguments) is { } hit)
            _dispatcher.TryEnqueue(() => Invoked?.Invoke(this, hit));
        else if (arguments.ContainsKey("open"))
            _dispatcher.TryEnqueue(() => Opened?.Invoke(this, kind));
    }

    private static string Escape(string text) => SecurityElement.Escape(text);
}
