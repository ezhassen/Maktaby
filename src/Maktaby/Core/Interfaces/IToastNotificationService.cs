using System;

namespace Maktaby.Core.Interfaces;

/// <summary>Outcome of asking for a toast. Three states, because "notifications are switched
/// off" and "the toast API itself failed" need OPPOSITE handling: the first means stay silent,
/// the second means fall back to a visible surface.
/// <para>
/// Note there is no "unsupported" state. <c>ToastNotifier.Setting</c> only ever reports
/// <c>Enabled</c> or one of five <c>Disabled*</c> values (user / application / group policy /
/// manifest), so the sole way to end up unable to toast is the call itself throwing — for
/// example a <c>COMException</c> when the AppUserModelID could not be claimed.
/// </para></summary>
public enum ToastShowResult
{
    /// <summary>The toast was delivered and will appear in the notification area.</summary>
    Shown = 0,

    /// <summary>Notifications are switched off — by the user, by group policy, or by our own
    /// manifest. All five <c>NotificationSetting.Disabled*</c> states land here. Do nothing and
    /// say nothing: re-announcing it another way would override a deliberate decision, whether
    /// that decision was the user's or an administrator's.</summary>
    Disabled = 1,

    /// <summary>This machine cannot show toasts at all (unsupported Windows build, policy,
    /// or the AppUserModelID could not be claimed). Nothing will be shown, so the caller
    /// should fall back to a visible surface.</summary>
    Unavailable = 2,
}

/// <summary>
/// Shows a transient Windows toast notification outside the app, without stealing focus or
/// opening a window.
/// </summary>
/// <remarks>
/// The toast click arrives on a THREADPOOL thread, so a caller that opens a WPF window from
/// <see cref="Show"/>'s callback MUST marshal to the UI thread first.
/// <para>
/// Used by the update checker for BACKGROUND checks only. A check the user explicitly asked
/// for opens the prompt directly, so a notification there would be redundant.
/// </para>
/// </remarks>
public interface IToastNotificationService
{
    /// <summary>
    /// Shows a notification. <paramref name="onClicked"/> runs when the user clicks it.
    /// </summary>
    /// <returns>What actually happened — see <see cref="ToastShowResult"/>.</returns>
    ToastShowResult Show(string title, string message, Action onClicked);
}
