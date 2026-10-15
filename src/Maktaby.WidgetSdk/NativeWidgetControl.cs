using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace Maktaby.WidgetSdk;

/// <summary>Convenience base: a <see cref="UserControl"/> that already implements
/// <see cref="INativeWidget"/>. Derive from this, build UI in the constructor (or load
/// loose XAML via <see cref="LoadXamlResource"/>), override the <c>On*</c> hooks, and
/// call the <c>Raise*</c> methods for the interaction events the host cannot see
/// (e.g. content hosted through an HWND airspace). For pure WPF visuals the host
/// detects mouse/focus itself — raising events is then optional.</summary>
    public class NativeWidgetControl : UserControl, INativeWidget, IWidgetSettingsProvider
    {
        public FrameworkElement Visual => this;

        public bool IsSuspended { get; private set; }

        /// <summary>Tag value marking a subtree that must never start a window drag. Set
        /// <c>Tag="no-drag"</c> on a button, tab or handle inside a widget whose own layout
        /// is clickable but whose background is draggable. Recognised on the pressed
        /// element and every ancestor up to this control.</summary>
        public const string NoDragTag = "no-drag";

        private Action<MouseButtonEventArgs>? _moveWindow;
        private bool _hostEngaged;

        /// <summary>Whether the user is currently engaging with this widget. The host pushes
        /// this whenever the picture changes; widgets use it to hide their own controls until
        /// the user is actually interacting (a weather widget showing nothing but its data is
        /// the common case). Starts false.
        ///
        /// This is deliberately NOT the same as "my window is active". The host's title bar
        /// and resize edges live in a SEPARATE chrome-overlay window, so dragging or resizing
        /// the widget leaves this window inactive while the user is plainly interacting with
        /// it. The host therefore treats chrome-visible as engaged too, and the controls stay
        /// up for the whole gesture instead of vanishing the moment a drag begins.</summary>
        protected bool IsHostEngaged => _hostEngaged;

        /// <summary>Called after <see cref="IsHostEngaged"/> changes, so a widget can show or
        /// hide interactive affordances without polling. Runs on the UI thread.</summary>
        protected virtual void OnHostInteractionChanged() { }

        internal void SetHostEngaged(bool engaged)
        {
            if (_hostEngaged == engaged) return;
            _hostEngaged = engaged;
            try { OnHostInteractionChanged(); } catch { }
        }

        /// <summary>Whether a left press on non-interactive content starts a window move.
        /// True by default, which is what makes a widget movable now that the host no longer
        /// needs a title bar for it. Set to false to take the decision over entirely, then
        /// call <see cref="MoveWindowOnMouseDown"/> from your own handler — useful when the
        /// widget knows better which regions are drag surfaces.</summary>
        protected bool MoveWindowOnMouseDownEnabled { get; set; } = true;

        public NativeWidgetControl()
        {
            // Declared here, in the BASE constructor, so it is always the first row in the
            // settings dialog and a derived widget cannot forget it or push it down. The host
            // renders it only when the manifest sets supportsTheme, and resolves it into the
            // theme pushed through ApplyTheme. "" (follow app) is the default, so existing
            // widgets keep behaving exactly as before until the user picks something.
            try
            {
                DefineSetting(ThemeSettingName, "Theme",
                    "Widget theme. Follow app uses the app or system theme.",
                    WidgetSettingKind.Theme, () => { }, defaultValue: ThemeKeys.FollowApp);
            }
            catch { }

            // On the control itself (== Visual), so a press is caught before any child control
            // can mark it handled, and OriginalSource still identifies what was pressed.
            PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDownForMove;
        }

        /// <summary>Name of the automatic theme setting declared by this class. The host looks
        /// it up by kind, but this is public so a widget can read its own value.</summary>
        public const string ThemeSettingName = "theme";

        /// <summary>The widget's own theme choice: <c>""</c> to follow the app, or one of
        /// <see cref="ThemeKeys"/>. Reading it is rarely necessary — the host pushes the
        /// resolved theme to <c>OnApplyTheme</c> — but it is exposed for widgets that want to
        /// know whether the user has pinned a theme.</summary>
        protected string ThemeSettingValue =>
            (FindSetting(ThemeSettingName)?.Value as string) ?? ThemeKeys.FollowApp;

        private void OnPreviewMouseLeftButtonDownForMove(object sender, MouseButtonEventArgs e)
        {
            if (!MoveWindowOnMouseDownEnabled || IsSuspended) return;
            // Right-click is the host's widget menu, never a move.
            if (e.ChangedButton != MouseButton.Left) return;
            if (IsDragBlocked(e.OriginalSource as DependencyObject)) return;
            MoveWindowOnMouseDown(e);
        }

        /// <summary>True when the press landed on something the user means to press rather
        /// than a drag surface: an interactive control, or a subtree tagged
        /// <see cref="NoDragTag"/>. Walks the whole ancestor chain, because the press usually
        /// targets the label inside the button rather than the button itself.
        ///
        /// Elements that are visually transparent but hit-testable report themselves as the
        /// source; they are NOT treated as blocking, because "transparent" describes how they
        /// paint, not whether they are meant to be pressed.</summary>
        private static bool IsDragBlocked(DependencyObject? source)
        {
            for (DependencyObject? node = source; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is ButtonBase or TextBoxBase or RangeBase or ComboBox) return true;
                if (node is FrameworkElement fe && ReferenceEquals(fe.Tag, NoDragTag)) return true;
            }
            return false;
        }

        /// <summary>Starts moving the window hosting this widget. The host drives the gesture
        /// itself from here (mouse capture, a movement threshold, snapping to screen edges
        /// and to other containers, and committing the new bounds to the model), which is why
        /// this is a request rather than a <c>DragMove</c> call: the native caption loop would
        /// snap to nothing and would let a double-click maximize the widget. A no-op when the
        /// widget is not hosted (a gallery preview, for instance).</summary>
        protected void MoveWindowOnMouseDown(MouseButtonEventArgs e)
        {
            try { _moveWindow?.Invoke(e); } catch { }
        }

        /// <summary>Host seam supplying <see cref="MoveWindowOnMouseDown(MouseButtonEventArgs)"/>;
        /// internal so a plugin cannot fake one. Null when running unhosted.</summary>
        internal void SetWindowMover(Action<MouseButtonEventArgs>? moveWindow) => _moveWindow = moveWindow;

        private readonly List<WidgetSetting> _settings = new();


    /// <summary>Settings declared via <see cref="DefineSetting"/> (empty by default).
    /// The host renders one editor per entry and writes back through
    /// <see cref="WidgetSetting.Value"/>.</summary>
    IReadOnlyList<WidgetSetting> IWidgetSettingsProvider.Settings => _settings;

    protected WidgetSetting DefineSetting(string name, string displayName, string description, WidgetSettingKind kind, Action onValueChanged, object? defaultValue = null, Dictionary<string, string>? listOfAvailableStrings = null, double? minimum = null, double? maximum = null)
    {
        if (_settings.Exists(s => s.Name == name)) throw new System.ArgumentException($"Duplicate widget setting '{name}'.", nameof(WidgetSetting));

        var setting = new WidgetSetting(name, displayName, description, kind, onValueChanged, defaultValue: defaultValue, listOfAvailableStrings: listOfAvailableStrings, minimum: minimum, maximum: maximum);
        _settings.Add(setting);
        return setting;
    }

    /// <summary>Declares a user-editable setting (call from the derived constructor).
    /// Names must be unique per widget; duplicates throw.</summary>
    protected WidgetSetting DefineSetting(WidgetSetting setting)
    {
        if (setting is null) throw new System.ArgumentNullException(nameof(setting));
        if (_settings.Exists(s => s.Name == setting.Name))
        {
            throw new System.ArgumentException($"Duplicate widget setting '{setting.Name}'.", nameof(setting));
        }

        _settings.Add(setting);
        return setting;
    }

    /// <summary>Finds a declared setting by name (<c>null</c> when absent).</summary>
    protected WidgetSetting? FindSetting(string name) =>
        _settings.Find(s => s.Name == name);

    /// <summary>Reads a declared setting's current value as <typeparamref name="T"/> (same
    /// shorthand as <c>FindSetting(name)?.GetBoolean() == true</c>, without the null dance).
    /// Returns <paramref name="defaultValue"/> when the setting is absent (e.g. read before
    /// its <see cref="DefineSetting"/> call) or the value cannot convert; never throws.
    /// Conversions use the invariant culture; enums parse from their string form
    /// (handy for <see cref="WidgetSettingKind.ListOfStrings"/> keys).</summary>
    protected T GetSettingsValue<T>(string name, T defaultValue)
    {
        try
        {
            var value = FindSetting(name)?.Value;
            if (value is null) return defaultValue;
            if (value is T typed) return typed;
            var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
            if (target.IsEnum && value is string s && System.Enum.TryParse(target, s, true, out var parsed))
                return (T)parsed;
            return (T)System.Convert.ChangeType(value, target, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return defaultValue;
        }
    }

    public void Suspend()
    {
        if (IsSuspended) return;
        IsSuspended = true;
        try { OnSuspend(); } catch { }
    }

    public void Resume()
    {
        if (!IsSuspended) return;
        IsSuspended = false;
        try { OnResume(); } catch { }
    }

    public void ApplyTheme(string? theme)
    {
        try { OnApplyTheme(theme); } catch { }
    }

    protected virtual void OnSuspend() { }
    protected virtual void OnResume() { }
    protected virtual void OnApplyTheme(string? theme) { }

    public event EventHandler? Entered;
    public event EventHandler? Left;
    public event EventHandler? Clicked;
    public event EventHandler<WidgetPointerEventArgs>? Pressed;
    public event EventHandler? Focused;
    public event EventHandler? Unfocused;

    protected void RaiseEntered() => Entered?.Invoke(this, EventArgs.Empty);
    protected void RaiseLeft() => Left?.Invoke(this, EventArgs.Empty);
    protected void RaiseClicked() => Clicked?.Invoke(this, EventArgs.Empty);
    protected void RaisePressed(double x, double y, WidgetPointerButton button) =>
        Pressed?.Invoke(this, new WidgetPointerEventArgs(x, y, button));
    protected void RaiseFocused() => Focused?.Invoke(this, EventArgs.Empty);
    protected void RaiseUnfocused() => Unfocused?.Invoke(this, EventArgs.Empty);

    /// <summary>Loads a loose <c>.xaml</c> file embedded as a manifest resource by the host
    /// compiler (resource name <c>&lt;slug&gt;/&lt;file&gt;.xaml</c>) and sets it as content.
    /// Named elements are reachable via <see cref="FrameworkElement.FindName"/> afterwards
    /// (loose XAML has no <c>x:Class</c> wiring).</summary>
    protected void LoadXamlResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"XAML resource '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        // Mark this control as the {sdk:Loc …} target owner while its loose XAML parses, so
        // extensions record against it. Restored in finally — nested loads can't happen here.
        var prev = CurrentConstructing;
        CurrentConstructing = this;
        try
        {
            var root = (FrameworkElement)XamlReader.Load(reader.BaseStream);
            Content = root;
        }
        finally
        {
            CurrentConstructing = prev;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        // Unhook everything that roots this instance: hide/show destroys and recreates the
        // host window every toggle, so a leaked handler pins the visual tree and the
        // collectible AssemblyLoadContext for the life of the process.
        try { PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDownForMove; } catch { }
        _moveWindow = null;
        if (_settings?.Any() == true)
        {
            foreach (var item in _settings)
            {
                item.Dispose();
            }
        }
        if (disposing && _loc is not null)
        {
            _loc.LanguageChanged -= OnWidgetLanguageChanged;
        }
    }

    #region Localization

    private IWidgetLocalization? _loc;

    // Targets recorded by {sdk:Loc …} during XAML parse, resolved once _loc is attached.
    private readonly List<(DependencyObject Target, DependencyProperty Property, string Key)> _locTargets = new();

    /// <summary>
    /// Called by the host right after construction to attach the widget's localization.
    /// Resolves every <c>{sdk:Loc …}</c> target recorded during the constructor's XAML parse
    /// and subscribes to <see cref="IWidgetLocalization.LanguageChanged"/> so a live app
    /// language switch re-translates them. Idempotent; safe to call with null (detaches).
    /// </summary>
    internal void SetLocalization(IWidgetLocalization? loc)
    {
        if (_loc is not null)
            _loc.LanguageChanged -= OnWidgetLanguageChanged;
        _loc = loc;
        if (loc is not null)
            loc.LanguageChanged += OnWidgetLanguageChanged;
        ResolveLocTargets();
        ApplyContentFlowDirection();
    }

    /// <summary>Records a <c>{sdk:Loc …}</c> target for later resolution. Called by
    /// <see cref="LocExtension"/> during parse; the owning control is the one whose
    /// constructor is running, which is always <c>this</c> on the UI thread.</summary>
    internal static void RegisterLocTarget(DependencyObject target, DependencyProperty property, string key)
    {
        // Resolve the control currently being constructed. During a plugin constructor the
        // only NativeWidgetControl on the stack is the one building its visual, so a
        // thread-static "current" set at the top of the base constructor is exact.
        var current = CurrentConstructing;
        current?._locTargets.Add((target, property, key));
    }

    // The control whose constructor is currently running (UI thread only).
    [System.ThreadStatic] private static NativeWidgetControl? CurrentConstructing;

    /// <summary>Localized text for <paramref name="key"/>. When no localization is attached
    /// (or the key is missing) returns <paramref name="defaultValue"/>. <paramref name="args"/>
    /// are formatted with the current culture.</summary>
    protected string L(string key, string defaultValue, params object?[] args)
        => _loc?.Get(key, defaultValue, args) ?? defaultValue;

    /// <summary>Plural form of <paramref name="key"/> for <paramref name="count"/>, falling
    /// back to <paramref name="defaultValue"/> when unresolved.</summary>
    protected string LPlural(string key, int count, string defaultValue, params object?[] args)
        => _loc?.Plural(key, count, defaultValue, args) ?? defaultValue;

    /// <summary>Content flow direction for this widget: the widget's own direction when its
    /// active xlf declares one, else the host app language's. Apply to the widget's root
    /// visual if the plugin wants its content mirrored (opt-in per the widget's xlf).</summary>
    protected FlowDirection ContentFlowDirection => _loc?.FlowDirection ?? FlowDirection.LeftToRight;

    private void ResolveLocTargets()
    {
        if (_loc is null || _locTargets.Count == 0) return;
        foreach (var (target, property, key) in _locTargets)
        {
            try
            {
                var text = _loc.Get(key, key);
                if (target.Dispatcher.CheckAccess())
                    target.SetValue(property, text);
                else
                    target.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try { target.SetValue(property, text); } catch { }
                    }));
            }
            catch { }
        }
    }

    private void ApplyContentFlowDirection()
    {
        // Assign whenever a localization is attached — unconditionally, not only when it says
        // RTL. Guarding on "is RTL" makes this a one-way latch: switching back to an
        // LTR language left the widget mirrored forever, because nothing ever reset the
        // property. With no localization attached the direction is the widget's own business
        // and is left alone.
        if (_loc is null) return;
        try { FlowDirection = ContentFlowDirection; } catch { }
    }

    private void OnWidgetLanguageChanged()
    {
        ResolveLocTargets();
        ApplyContentFlowDirection();
        try { OnLanguageChanged(); } catch { }
    }

    /// <summary>Called after the active language changed (host switch or initial attach).
    /// Override to re-apply strings the widget built in code (tooltips, labels) that are not
    /// <c>{sdk:Loc …}</c> targets. Runs on the UI thread.</summary>
    protected virtual void OnLanguageChanged() { }

    #endregion
}
