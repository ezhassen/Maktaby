using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Maktaby.Views;

/// <summary>
/// Small "About" dialog: app name, live version (from assembly metadata, incl. prerelease labels),
/// author/copyright, and a link to the GitHub project page. The GitHub logo follows the applied
/// app theme (dark variant on dark, light variant on light).
/// </summary>
public partial class AboutView : AppWindows.AppFluentWindow
{
    public AboutView()
    {
        InitializeComponent();
        // Preserve this dialog's compact chrome (was set on its own TitleBar before).
        TitleBar.Height = 26;
        TitleBar.ShowMaximize = false;
        PopulateAssemblyInfo();
        ApplyGithubLogoTheme();

        // Track live theme switches while the window is open.
        ApplicationThemeManager.Changed += OnAppThemeChanged;
        Closed += (_, _) => ApplicationThemeManager.Changed -= OnAppThemeChanged;
    }

    private void OnAppThemeChanged(ApplicationTheme currentTheme, System.Windows.Media.Color systemAccent)
    {
        ApplyGithubLogoTheme();
    }

    private void ApplyGithubLogoTheme()
    {
        var isDark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        GithubLogo.Source = new BitmapImage(
            new Uri($"pack://application:,,,/Assets/github_{(isDark ? "dark" : "light")}.png"));
    }

    private void PopulateAssemblyInfo()
    {
        // AppVersion is the single reader of the running build's version: the About window and
        // the update checker must never be able to disagree about what is installed. MinVer
        // writes InformationalVersion as "1.0.2-beta+88c49b5"; the '+sha' is dropped there.
        VersionText.Text = $"Version {Helpers.AppVersion.Current}";

        var entry = Assembly.GetEntryAssembly();
        var copyright = entry?.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright;
        CopyrightText.Text = copyright ?? string.Empty;

        var company = entry?.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
        if (!string.IsNullOrEmpty(company))
        {
            AuthorText.Text = $"© {company}";
        }
    }

    private void ProjectLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        // UseShellExecute=true is required on .NET Core+ so the default browser opens the URL.
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    /// <summary>ESC closes the window from anywhere (works even with no focused element).</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    /// <summary>Drag the window by pressing anywhere EXCEPT: the TitleBar (it drags itself via its
    /// own chrome) and the GitHub Hyperlink (its click must open the browser, not move the window).
    /// Everything else — labels, icons, separators, empty space — becomes a drag handle.</summary>
    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Documents.Hyperlink)
        {
            return;
        }

        if (IsWithinAncestor(e.OriginalSource as DependencyObject, TitleBar))
        {
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // DragMove throws when the left button was already released; harmless.
            }
        }
    }

    /// <summary>Walks BOTH visual and logical parents (hyperlink inlines live only in the logical
    /// tree), reporting whether <paramref name="node"/> sits inside <paramref name="ancestor"/>.</summary>
    private static bool IsWithinAncestor(DependencyObject? node, DependencyObject? ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }

            var visual = VisualTreeHelper.GetParent(node);
            node = visual ?? LogicalTreeHelper.GetParent(node);
        }

        return false;
    }
}
