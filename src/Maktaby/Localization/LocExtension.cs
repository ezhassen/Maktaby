using System.Windows;
using System.Windows.Markup;

namespace Maktaby.Localization;

/// <summary>
/// Markup extension that resolves a localization key at parse time and re-translates it
/// live when the language changes. XAML:
/// <code>
/// &lt;TextBlock Text="{loc:LocKey Section.Settings.Appearance}" /&gt;
/// &lt;TextBlock Text="{loc:LocPlural Boxes.Count, Value=3}" /&gt;
/// </code>
/// <para>
/// Live switching: the extension subscribes to <see cref="Loc.LanguageChanged"/> and pushes
/// the new string onto the target property when the language changes, so an open window
/// re-translates without being reopened. Unsubscribes when the target element unloads, so a
/// closed window never leaks the handler.
/// </para>
/// <para>
/// Named <c>LocKey</c>/<c>LocPlural</c> rather than <c>Key</c>/<c>Plural</c> to avoid the
/// <see cref="System.Windows.Input.Key"/> collision in code that imports both namespaces.
/// </para>
/// </summary>
public class LocKey : MarkupExtension
{
    private bool _subscribed;
    private WeakReference<DependencyObject>? _target;
    private DependencyProperty? _property;
    private object?[] _args = Array.Empty<object?>();
    private bool _plural;
    private int _count;

    /// <summary>Creates an extension for <paramref name="key"/> — the XAML positional form
    /// <c>{loc:LocKey Section.Settings.Appearance}</c>.</summary>
    public LocKey(string key)
    {
        Id = key;
    }

    /// <summary>Creates an empty extension (used by the XAML parser before setting properties,
    /// e.g. <c>{loc:LocKey Id=Section.Settings.Appearance}</c>).</summary>
    public LocKey() { }

    /// <summary>The localization key (the positional constructor argument in XAML).</summary>
    public string? Id { get; set; }

    /// <summary>Optional format arguments, applied under the current culture.</summary>
    public object?[] Args
    {
        get => _args;
        set => _args = value ?? Array.Empty<object?>();
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Id))
            return string.Empty;

        _plural = false;
        _count = 0;

        Attach(serviceProvider);
        return Resolve();
    }

    internal void Attach(IServiceProvider serviceProvider)
    {
        // IProvideValueTarget lets us re-push the string on a language switch. Absent for
        // non-visual targets (designers / bindings): plain static text then, still correct.
        if (serviceProvider.GetService(typeof(IProvideValueTarget)) is IProvideValueTarget pvt
            && pvt.TargetObject is DependencyObject target
            && pvt.TargetProperty is DependencyProperty property)
        {
            _target = new WeakReference<DependencyObject>(target);
            _property = property;
            Subscribe(target);
        }
    }

    internal string Resolve() => Loc.Get(Id!, _args);

    internal void Push()
    {
        var text = _plural ? Loc.Plural(Id!, _count, _args) : Loc.Get(Id!, _args);
        if (_target is not null && _target.TryGetTarget(out var d) && _property is not null && d is not null)
        {
            if (d.Dispatcher.CheckAccess())
                d.SetValue(_property, text);
            else
                d.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { d.SetValue(_property, text); } catch { }
                }));
        }
    }

    internal void SetPlural(bool plural, int count)
    {
        _plural = plural;
        _count = count;
    }

    private void Subscribe(DependencyObject target)
    {
        if (_subscribed) return;
        _subscribed = true;
        Loc.LanguageChanged += Push;

        // Self-unsubscribe when the target element goes away, so a closed window's
        // extensions never accumulate on the static event.
        if (target is FrameworkElement fe)
            fe.Unloaded += (_, _) => Loc.LanguageChanged -= Push;
    }
}

/// <summary>
/// Markup extension for plural units: <c>{loc:LocPlural Boxes.Count, Value=3}</c> selects
/// the CLDR plural category for <see cref="Value"/> and re-translates on a language switch.
/// </summary>
public class LocPlural : LocKey
{
    /// <summary>Creates a plural extension for <paramref name="key"/>.</summary>
    public LocPlural(string key) : base(key)
    {
        Value = 0;
    }

    /// <summary>Creates an empty plural extension (XAML parser property form).</summary>
    public LocPlural() { }

    /// <summary>Count whose CLDR plural category selects the target segment.</summary>
    public int Value { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        Attach(serviceProvider);
        SetPlural(true, Value);
        return Loc.Plural(Id!, Value);
    }
}
