using Maktaby.Core.Interfaces;
using Maktaby.Native;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Maktaby.Win32.Services;

/// <summary>
/// Shows a Windows toast notification and reports a click back to the caller.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="ToastNotificationManager"/> — the real Windows notification channel — rather
/// than the older <c>Shell_NotifyIcon</c> balloon. The balloon path was tried first and
/// measured on Windows 11: <c>NIM_ADD</c> returned TRUE in every flag combination, yet no
/// balloon was ever displayed, and it added a permanent blank entry to the notification area.
/// A toast needs no tray icon, renders where users expect on Windows 11, and needs no extra
/// NuGet package because the projections ship with the app's <c>net10.0-windows10.0.*</c> TFM.
/// </para>
/// <para>
/// An unpackaged app must claim an <b>AppUserModelID</b> or the notification is attributed to the
/// shell and dropped, which is why <see cref="RegisterAppUserModelId"/> runs first.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class ToastNotificationService : IToastNotificationService, IDisposable
{
    /// <summary>Must match the AppUserModelId key written below.</summary>
    private const string AppUserModelId = "Maktaby.Desktop";

    /// <summary>
    /// Stable Tag and Group so successive update toasts REPLACE one another instead of piling
    /// up. Windows keys notification replacement on Tag+Group together: a new toast carrying
    /// the same pair supersedes the previous one in Action Center. Neither value includes the
    /// version, because that would give every release its own slot and the stack would grow
    /// again. A constant pair means at most ONE update toast exists at any time.
    /// </summary>
    private const string UpdateToastTag = "maktaby.update";
    private const string UpdateToastGroup = "maktaby.update";

    private ToastNotifier? _notifier;

    /// <summary>Held so the <see cref="ToastNotification"/> - and the click handler attached to
    /// it - is not collected while the toast is on screen.</summary>
    private ToastNotification? _liveToast;

    private bool _disposed;

    public ToastNotificationService()
    {
        RegisterAppUserModelId();
    }

    /// <summary>
    /// Claims an AppUserModelID for this process: writes the HKCU key the shell looks up and
    /// tells the shell this process owns it. HKCU, so no elevation is needed. Best-effort — a
    /// failure here costs the notification, never the app.
    /// </summary>
    private static void RegisterAppUserModelId()
    {
        try
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                       $@"Software\Classes\AppUserModelId\{AppUserModelId}"))
            {
                if (key is not null)
                {
                    key.SetValue(null, "Maktaby");
                    key.SetValue("DisplayName", "Maktaby");
                }
            }

            int hr = Shell32.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            Logging.Log.Information(
                "Toast notifications: AppUserModelId '{Id}' registered (hr=0x{Hr:X8})",
                AppUserModelId,
                (uint)hr);
        }
        catch (Exception ex)
        {
            Logging.Log.Warning(ex, "Could not register the AppUserModelId; toasts may not appear");
        }
    }

    public ToastShowResult Show(string title, string message, Action onClicked)
    {
        if (_disposed) { return ToastShowResult.Unavailable; }

        try
        {
            var notifier = _notifier ??= ToastNotificationManager.CreateToastNotifier(AppUserModelId);

            // The single most important check: with notifications switched off, notifier.Show()
            // does NOT throw — it silently does nothing, so without this the app would log
            // "notified" and the user would see nothing. Every non-Enabled value here
            // (DisabledForUser / ForApplication / ByGroupPolicy / ByManifest) is a deliberate
            // switch-off and is reported as such, never overridden with a fallback.
            var setting = notifier.Setting;
            if (setting != NotificationSetting.Enabled)
            {
                Logging.Log.Information(
                    "Toast not shown: notifications are {Setting} for {AppUserModelId}",
                    setting, AppUserModelId);
                return ToastShowResult.Disabled;
            }

            // Escape the text: it is interpolated into XML, and a version string is trusted
            // but a release note is not.
            var payload =
                "<toast activationType=\"foreground\" duration=\"short\">" +
                "<visual><binding template=\"ToastGeneric\">" +
                $"<text>{Escape(title)}</text>" +
                $"<text>{Escape(message)}</text>" +
                "</binding></visual></toast>";

            var xml = new XmlDocument();
            xml.LoadXml(payload);

            var toast = new ToastNotification(xml)
            {
                Tag = UpdateToastTag,
                Group = UpdateToastGroup,
            };
            toast.Activated += (_, _) =>
            {
                try { onClicked(); }
                catch (Exception ex) { Logging.Log.Warning(ex, "Toast click handler failed"); }
            };

            _liveToast = toast;
            notifier.Show(toast);

            Logging.Log.Information("Toast notification shown: {Title}", title);
            return ToastShowResult.Shown;
        }
        catch (Exception ex)
        {
            // Includes NotSupportedException on a Windows build without the toast platform,
            // and COMException when the AppUserModelId could not be claimed.
            _liveToast = null;
            Logging.Log.Warning(ex, "Could not show the toast notification");
            return ToastShowResult.Unavailable;
        }
    }

    private static string Escape(string? value) => (value ?? string.Empty)
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;

        if (_liveToast is not null)
        {
            try { _liveToast.Activated -= null; } catch { /* best effort */ }
            _liveToast = null;
        }

        _notifier = null;
    }
}
