using Maktaby.Core.Models;
using ICSharpCode.AvalonEdit.Document;
using System.ComponentModel.DataAnnotations;

namespace Maktaby.ViewModels;

/// <summary>
/// Backing state for <see cref="Views.WidgetDataWindow"/>: manifest fields plus the three
/// editor documents. Editors bind <c>Document</c> (a dependency property — <c>Text</c> is
/// not bindable), so content flows through XAML bindings and renders in any host that
/// evaluates bindings, with no code-behind fill required. Edits mutate the shared
/// <see cref="TextDocument"/> instances in place, so readers always see current text.
/// </summary>
public sealed class WidgetDataViewModel : ViewModelBase
{
    private string _name = "";

    /// <summary>Becomes a folder name: validated explicitly via <see cref="Validate"/>
    /// (not per keystroke) using native data-annotation errors.</summary>
    [CustomValidation(typeof(WidgetDataViewModel), nameof(ValidateWidgetSlug))]
    public string Name { get => _name; set => SetField(ref _name, value); }

    /// <summary>Answers the duplicate question (window wires the widget service + new-mode
    /// flag; null skips it — e.g. edit mode where renaming is disabled, or design).</summary>
    public Func<string, bool>? SlugExists { get; set; }

    public static ValidationResult? ValidateWidgetSlug(string? value, ValidationContext context)
    {
        string name = (value ?? "").Trim();
        if (name.Length == 0)
            return new ValidationResult("Name is required.");
        var bad = name.Distinct().Where(c => System.IO.Path.GetInvalidFileNameChars().Contains(c)).ToArray();
        if (bad.Length > 0)
            return new ValidationResult($"Name contains invalid characters: {string.Join(" ", bad.Select(c => $"'{c}'"))}.");
        if (name.EndsWith('.') || name.EndsWith(' '))
            return new ValidationResult("Name cannot end with a dot or space.");
        if (name is "." or "..")
            return new ValidationResult("Name is reserved.");
        string stem = name.ToUpperInvariant().Split('.')[0];
        if (stem is "CON" or "PRN" or "AUX" or "NUL"
            || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            return new ValidationResult($"'{stem}' is a reserved Windows name.");
        if (context.ObjectInstance is WidgetDataViewModel vm && vm.SlugExists != null)
        {
            bool exists = false;
            try { exists = vm.SlugExists(name); } catch { }
            if (exists)
                return new ValidationResult($"A widget named '{name}' already exists.");
        }
        return ValidationResult.Success;
    }

    /// <summary>Explicit validation entry point (Save): runs all rules, errors surface
    /// through <see cref="ObservableValidator.HasErrors"/> for native WPF display.</summary>
    public bool Validate()
    {
        try { ValidateAllProperties(); } catch { }
        return !HasErrors;
    }

    private string _author = "";
    public string Author { get => _author; set => SetField(ref _author, value); }

    private string _description = "";
    public string Description { get => _description; set => SetField(ref _description, value); }

    private string _version = "1.0.0";
    public string Version { get => _version; set => SetField(ref _version, value); }

    private string _widthText = "300";
    public string WidthText { get => _widthText; set => SetField(ref _widthText, value); }

    private string _heightText = "220";
    public string HeightText { get => _heightText; set => SetField(ref _heightText, value); }

    private bool _resizable = true;
    public bool Resizable { get => _resizable; set => SetField(ref _resizable, value); }

    private bool _allowNetwork;
    public bool AllowNetwork { get => _allowNetwork; set => SetField(ref _allowNetwork, value); }

    private bool? _canSwitchTheme;
    public bool? CanSwitchTheme { get => _canSwitchTheme; set => SetField(ref _canSwitchTheme, value); }

    private TextDocument _htmlDocument = new();
    public TextDocument HtmlDocument { get => _htmlDocument; set => SetField(ref _htmlDocument, value); }

    private TextDocument _cssDocument = new();
    public TextDocument CssDocument { get => _cssDocument; set => SetField(ref _cssDocument, value); }

    private TextDocument _jsDocument = new();
    public TextDocument JsDocument { get => _jsDocument; set => SetField(ref _jsDocument, value); }

    public string HtmlText => HtmlDocument?.Text ?? "";
    public string CssText => CssDocument?.Text ?? "";
    public string JsText => JsDocument?.Text ?? "";

    /// <summary>Sample content: editor defaults, optionally with blank identity fields
    /// (new-widget mode leaves name/author/description empty).</summary>
    public void LoadSampleData(bool blankIdentity = false)
    {
        if (blankIdentity)
        {
            Name = "";
            Author = "";
            Description = "";
        }
        else
        {
            Name = "MyWidget";
            Author = "Author";
            Description = "Smooth sweeping clock face.";
        }
        Version = "1.0.0";
        WidthText = "300";
        HeightText = "220";
        Resizable = true;
        AllowNetwork = false;
        CanSwitchTheme = null;
        HtmlDocument = new TextDocument("<div style=\"display:flex;align-items:center;justify-content:center;height:100%;font-family:sans-serif;font-size:18px;\">Hello Widget</div>");
        CssDocument = new TextDocument("body { margin:0; background:transparent; }");
        JsDocument = new TextDocument("// console.log('loaded');");
    }

    public void LoadFrom(string slug, WebWidgetManifest manifest, string html, string css, string js)
    {
        Name = slug;
        Author = manifest.Author ?? "";
        Description = manifest.Description ?? "";
        Version = manifest.Version ?? "1.0.0";
        WidthText = (manifest.Width ?? 300).ToString();
        HeightText = (manifest.Height ?? 220).ToString();
        Resizable = manifest.IsResizable;
        AllowNetwork = manifest.IsNetworkAllowed;
        CanSwitchTheme = manifest.CanSwitchTheme;
        HtmlDocument = new TextDocument(html);
        CssDocument = new TextDocument(css);
        JsDocument = new TextDocument(js);
    }

    public WebWidgetManifest BuildManifest()
    {
        int.TryParse(WidthText, out int w);
        int.TryParse(HeightText, out int h);
        return new WebWidgetManifest
        {
            Name = string.IsNullOrWhiteSpace(Name) ? "widget" : Name.Trim(),
            Author = Author?.Trim(),
            Description = Description?.Trim(),
            Version = Version?.Trim(),
            Width = w > 0 ? w : 300,
            Height = h > 0 ? h : 220,
            Resizable = Resizable,
            AllowNetwork = AllowNetwork,
            CanSwitchTheme = CanSwitchTheme
        };
    }
}
