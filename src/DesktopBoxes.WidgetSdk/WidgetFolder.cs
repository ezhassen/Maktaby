using System.IO;

namespace DesktopBoxes.WidgetSdk;

/// <summary>Which loader owns a widget folder. Detection is by manifest <em>filename</em> —
/// <c>nwidget.json</c> means native, anything else (including a missing manifest) is CSS.
/// No assembly is ever loaded, no code runs.</summary>
public enum WidgetFolderKind
{
    Unknown,
    Css,
    Native,
}

/// <summary>Single shared implementation of kind detection, used by both the CSS and the
/// native loader so a folder can never be claimed twice or missed by both.</summary>
public static class WidgetFolder
{
    public const string ManifestFileName = "widget.json";
    public const string NativeManifestFileName = "nwidget.json";

    public static WidgetFolderKind PeekKind(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return WidgetFolderKind.Unknown;
            return File.Exists(Path.Combine(folder, NativeManifestFileName))
                ? WidgetFolderKind.Native
                : WidgetFolderKind.Css; // legacy folders have no manifest at all
        }
        catch
        {
            return WidgetFolderKind.Css;
        }
    }
}
