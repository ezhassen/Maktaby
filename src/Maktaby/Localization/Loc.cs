using System.Windows;
using System.Windows.Media;
using Maktaby.Core.Interfaces;

namespace Maktaby.Localization;

/// <summary>
/// Static facade over the injected <see cref="ILocalizationProvider"/> (resolved from
/// <c>App.Services</c>). Used from XAML (<c>{loc:Key …}</c>, see <see cref="LocExtension"/>)
/// and from code (<c>Loc.Get("…")</c>). Also owns the FlowDirection walk, which is applied
/// per surface — never at Window level.
/// <para>
/// Every method is safe to call before the provider is registered (design time, very early
/// startup): they return the key / do nothing instead of throwing.
/// </para>
/// </summary>
public static class Loc
{
    private static ILocalizationProvider? _provider;

    /// <summary>The registered provider, or null before DI is up.</summary>
    public static ILocalizationProvider? Provider
    {
        get
        {
            if (_provider is not null) return _provider;
            try
            {
                _provider = Maktaby.App.Services?.GetService(typeof(ILocalizationProvider)) as ILocalizationProvider;
            }
            catch
            {
                // Design time / pre-DI: stay null, everything degrades gracefully.
            }
            return _provider;
        }
    }

    /// <summary>Active language, or null before initialization.</summary>
    public static LanguageInfo? Current => Provider?.Current;

    /// <summary>Culture used for formatting.</summary>
    public static System.Globalization.CultureInfo Culture => Provider?.Culture ?? System.Globalization.CultureInfo.CurrentCulture;

    /// <summary>All discovered languages for the Settings picker.</summary>
    public static IReadOnlyList<LanguageInfo> AvailableLanguages => Provider?.AvailableLanguages ?? Array.Empty<LanguageInfo>();

    /// <summary>Localized text for <paramref name="key"/> (current → English → <c>[key]</c>),
    /// formatted with <paramref name="args"/> under the current culture.</summary>
    public static string Get(string key, params object?[] args)
        => Provider?.Get(key, args) ?? $"[{key}]";

    /// <summary>The English text for <paramref name="key"/> (the source language), regardless of
    /// the active language. Used for bilingual headers ("اللغة (Language)"). Returns empty when
    /// there is no English text (or the key is missing).</summary>
    public static string GetEnglish(string key)
        => Provider?.GetEnglish(key) ?? string.Empty;

    /// <summary>Plural form of <paramref name="key"/> for <paramref name="count"/>.</summary>
    public static string Plural(string key, int count, params object?[] args)
        => Provider?.Plural(key, count, args) ?? $"[{key}]";

    /// <summary>Switches the active language and raises <see cref="LanguageChanged"/>.</summary>
    public static void SetLanguage(string code) => Provider?.SetLanguage(code);

    /// <summary>Raised after the active language changed: strings, culture and flow direction
    /// are all settled. XAML markup extensions, converters and code-side labels re-translate here.</summary>
    public static event Action? LanguageChanged;

    /// <summary>Bridges the provider's event to the static <see cref="LanguageChanged"/>.
    /// Called once from <see cref="Maktaby.App"/> after DI is built.</summary>
    internal static void Attach(ILocalizationProvider provider)
    {
        _provider = provider;
        provider.LanguageChanged += RaiseLanguageChanged;
    }

    private static void RaiseLanguageChanged()
    {
        var handler = LanguageChanged;
        if (handler is null) return;
        foreach (var h in handler.GetInvocationList())
        {
            try { ((Action)h)(); }
            catch { /* one broken subscriber must not stop the switch */ }
        }
    }

    #region FlowDirection (per surface, never Window level)

    /// <summary>Flow direction of the active language, default LeftToRight.</summary>
    public static FlowDirection CurrentFlowDirection
        => Current?.FlowDirection == TextFlowDirection.RightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

    #endregion

    #region Window helper

    /// <summary>
    /// Applies the active language's content flow direction (LTR/RTL) to a window's ROOT
    /// CHILD only, and re-applies it on every language switch. Call once from a window
    /// constructor (after <c>InitializeComponent</c>).
    /// <para>
    /// The Window itself keeps the Windows OS flow direction (the default it was created
    /// with) — only the single root child of <c>Window.Content</c> is mirrored to the
    /// language. Everything below that root inherits from it, so the whole content area
    /// flips together while the window chrome (title bar, borders) stays as Windows set it.
    /// This is what lets an English Windows host an Arabic session: window LTR, content RTL.
    /// </para>
    /// <para>
    /// The subscription self-detaches when the window closes, so a closed window never leaks
    /// the handler.
    /// </para>
    /// </summary>
    public static void ApplyTo(Window window)
    {
        if (window is null) return;
        Apply(window);
        void OnChanged()
        {
            try { Apply(window); }
            catch { }
        }
        LanguageChanged += OnChanged;
        window.Closed += (_, _) => LanguageChanged -= OnChanged;
    }

    /// <summary>Sets <see cref="FrameworkElement.FlowDirection"/> on the ROOT CHILD of the
    /// window's content only (not the Window, not deeper descendants). The root child's
    /// direction cascades to its whole subtree via inheritance. The Window itself is always
    /// pinned LTR so its chrome (title bar, buttons, borders) never mirrors — only content
    /// follows the language.</summary>
    private static void Apply(Window window)
    {
        try
        {
            // Window chrome is always LTR, regardless of OS or app language.
            window.FlowDirection = FlowDirection.LeftToRight;

            var content = window.Content as DependencyObject;
            if (content is null) return;

            // Content itself may be a FrameworkElement (the root child) — set it directly.
            if (content is FrameworkElement root)
            {
                root.FlowDirection = CurrentFlowDirection;
                return;
            }

            // Otherwise set FlowDirection on each immediate child of the content panel.
            var count = VisualTreeHelper.GetChildrenCount(content);
            for (int i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(content, i) is FrameworkElement child)
                    child.FlowDirection = CurrentFlowDirection;
            }
        }
        catch { }
    }

    #endregion
}
