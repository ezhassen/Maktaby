using DesktopBoxesUI.Core.Models;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace CSSWidgetsCreator
{
    public partial class MainWindow
    {
        private string? _currentFolder;
        private readonly DispatcherTimer _debounce;
        private bool _isUpdatingFields;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public MainWindow()
        {
            InitializeComponent();
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _debounce.Tick += (_, _) => { _debounce.Stop(); RefreshPreview(); };

            Loaded += OnLoaded;
            HtmlBox.TextChanged += (_, _) => _debounce.Start();
            CssBox.TextChanged += (_, _) => _debounce.Start();
            JsBox.TextChanged += (_, _) => _debounce.Start();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Default new widget
            SetDefaultContent();
            UpdateManifestBox();
            RefreshPreview();
            UpdatePreviewSizeLabel();
            ApplyTheme("System");
        }

        private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Ignore firing during initialization before Loaded
            if (!IsLoaded) return;
            if (ThemeSelector?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                ApplyTheme(tag);
            else if (e.AddedItems.Count > 0 && e.AddedItems[0] is ComboBoxItem added && added.Tag is string tag2)
                ApplyTheme(tag2);
        }

        private void ApplyTheme(string tag)
        {
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.Invoke(() => ApplyTheme(tag));
                    return;
                }

                switch (tag)
                {
                    case "Light":
                        ApplicationThemeManager.Apply(ApplicationTheme.Light, Wpf.Ui.Controls.WindowBackdropType.Mica);
                        break;
                    case "Dark":
                        ApplicationThemeManager.Apply(ApplicationTheme.Dark, Wpf.Ui.Controls.WindowBackdropType.Mica);
                        break;
                    default:
                        ApplicationThemeManager.ApplySystemTheme(true);
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Theme switch failed: {ex.Message}");
            }
        }

        private void SetDefaultContent()
        {
            NameBox.Text = "MyWidget";
            AuthorBox.Text = "";
            DescBox.Text = "";
            VersionBox.Text = "1.0.0";
            WidthBox.Text = "300";
            HeightBox.Text = "220";
            ResizableBox.IsChecked = true;
            NetworkBox.IsChecked = false;
            HtmlBox.Text = "<div style=\"display:flex;align-items:center;justify-content:center;height:100%;font-family:sans-serif;font-size:18px;background:#f0f0f0;border-radius:8px;\">Hello Widget</div>";
            CssBox.Text = "body { margin:0; background:transparent; }\n* { box-sizing:border-box; }";
            JsBox.Text = "// console.log('loaded');\ndocument.body.addEventListener('click', () => console.log('clicked'));";
            _currentFolder = null;
            CurrentFolderLabel.Text = "No folder loaded – New widget";
        }

        private CssWidgetManifest BuildManifestFromFields()
        {
            int.TryParse(WidthBox.Text, out int w);
            int.TryParse(HeightBox.Text, out int h);
            return new CssWidgetManifest
            {
                Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "widget" : NameBox.Text.Trim(),
                Author = AuthorBox.Text?.Trim(),
                Description = DescBox.Text?.Trim(),
                Version = VersionBox.Text?.Trim(),
                Width = w > 0 ? w : 300,
                Height = h > 0 ? h : 220,
                Resizable = ResizableBox.IsChecked ?? true,
                AllowNetwork = NetworkBox.IsChecked ?? false
            };
        }

        private void UpdateManifestBox()
        {
            try
            {
                var manifest = BuildManifestFromFields();
                ManifestBox.Text = JsonSerializer.Serialize(manifest, JsonOpts);
            }
            catch { }
        }

        private void ManifestField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingFields) return;
            UpdateManifestBox();
            _debounce.Start();
            // Sync preview size sliders to manifest size when user edits Width/Height
            if (sender == WidthBox && int.TryParse(WidthBox.Text, out int w) && w >= 150 && w <= 600)
            {
                PreviewWidthSlider.ValueChanged -= PreviewSizeSlider_ValueChanged;
                PreviewWidthSlider.Value = w;
                PreviewWidthSlider.ValueChanged += PreviewSizeSlider_ValueChanged;
                PreviewFrame.Width = w;
                UpdatePreviewSizeLabel();
            }
            if (sender == HeightBox && int.TryParse(HeightBox.Text, out int h) && h >= 150 && h <= 600)
            {
                PreviewHeightSlider.ValueChanged -= PreviewSizeSlider_ValueChanged;
                PreviewHeightSlider.Value = h;
                PreviewHeightSlider.ValueChanged += PreviewSizeSlider_ValueChanged;
                PreviewFrame.Height = h;
                UpdatePreviewSizeLabel();
            }
        }

        private void RefreshPreview()
        {
            UpdateManifestBox();
            var html = HtmlBox.Text ?? "";
            var css = CssBox.Text ?? "";
            var js = JsBox.Text ?? "";
            var manifest = BuildManifestFromFields();
            try
            {
                PreviewControl.LoadDirect(html, css, js, manifest);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Preview failed: {ex.Message}");
            }
        }

        private void PreviewRefresh_Click(object sender, RoutedEventArgs e) => RefreshPreview();

        private void PreviewSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (PreviewFrame == null) return;
            PreviewFrame.Width = PreviewWidthSlider.Value;
            PreviewFrame.Height = PreviewHeightSlider.Value;
            UpdatePreviewSizeLabel();
        }

        private void UpdatePreviewSizeLabel()
        {
            if (PreviewSizeLabel != null && PreviewFrame != null)
                PreviewSizeLabel.Text = $"{(int)PreviewFrame.Width} × {(int)PreviewFrame.Height}";
        }

        private void ResetPreviewSize_Click(object sender, RoutedEventArgs e)
        {
            if (int.TryParse(WidthBox.Text, out int w) && int.TryParse(HeightBox.Text, out int h))
            {
                PreviewWidthSlider.Value = Math.Clamp(w, 150, 600);
                PreviewHeightSlider.Value = Math.Clamp(h, 150, 600);
                PreviewFrame.Width = PreviewWidthSlider.Value;
                PreviewFrame.Height = PreviewHeightSlider.Value;
                UpdatePreviewSizeLabel();
            }
        }

        private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newW = Math.Clamp(PreviewFrame.Width + e.HorizontalChange, 150, 800);
            double newH = Math.Clamp(PreviewFrame.Height + e.VerticalChange, 150, 800);
            PreviewFrame.Width = newW;
            PreviewFrame.Height = newH;
            // Update sliders without triggering recursive ValueChanged loop
            PreviewWidthSlider.ValueChanged -= PreviewSizeSlider_ValueChanged;
            PreviewHeightSlider.ValueChanged -= PreviewSizeSlider_ValueChanged;
            PreviewWidthSlider.Value = newW;
            PreviewHeightSlider.Value = newH;
            PreviewWidthSlider.ValueChanged += PreviewSizeSlider_ValueChanged;
            PreviewHeightSlider.ValueChanged += PreviewSizeSlider_ValueChanged;
            UpdatePreviewSizeLabel();
        }

        private void New_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Discard current changes and create new widget?", "New Widget", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (result == MessageBoxResult.OK)
            {
                SetDefaultContent();
                UpdateManifestBox();
                RefreshPreview();
                PreviewWidthSlider.Value = 300;
                PreviewHeightSlider.Value = 220;
            }
        }

        private void LoadFromFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Widget Folder (containing index.html, style.css, script.js, widget.json)",
                Multiselect = false
            };
            if (dlg.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dlg.FolderName)) return;
            LoadFromFolder(dlg.FolderName);
        }

        private void LoadFromFolder(string folder)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    MessageBox.Show($"Folder not found:\n{folder}", "Load Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var htmlPath = Path.Combine(folder, "index.html");
                var cssPath = Path.Combine(folder, "style.css");
                var jsPath = Path.Combine(folder, "script.js");
                var manifestPath = Path.Combine(folder, "widget.json");

                string html = File.Exists(htmlPath) ? File.ReadAllText(htmlPath, Encoding.UTF8) : "";
                string css = File.Exists(cssPath) ? File.ReadAllText(cssPath, Encoding.UTF8) : "";
                string js = File.Exists(jsPath) ? File.ReadAllText(jsPath, Encoding.UTF8) : "";

                CssWidgetManifest manifest;
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        var json = File.ReadAllText(manifestPath, Encoding.UTF8);
                        manifest = JsonSerializer.Deserialize<CssWidgetManifest>(json, JsonOpts) ?? CssWidgetManifest.DefaultFor(Path.GetFileName(folder));
                    }
                    catch
                    {
                        manifest = CssWidgetManifest.DefaultFor(Path.GetFileName(folder));
                    }
                }
                else
                {
                    manifest = CssWidgetManifest.DefaultFor(Path.GetFileName(folder));
                }
                manifest.Name ??= Path.GetFileName(folder);

                _isUpdatingFields = true;
                try
                {
                    _currentFolder = folder;
                    CurrentFolderLabel.Text = folder;
                    NameBox.Text = manifest.Name ?? Path.GetFileName(folder);
                    AuthorBox.Text = manifest.Author ?? "";
                    DescBox.Text = manifest.Description ?? "";
                    VersionBox.Text = manifest.Version ?? "1.0.0";
                    WidthBox.Text = (manifest.Width ?? 300).ToString();
                    HeightBox.Text = (manifest.Height ?? 220).ToString();
                    ResizableBox.IsChecked = manifest.IsResizable;
                    NetworkBox.IsChecked = manifest.IsNetworkAllowed;
                    HtmlBox.Text = html;
                    CssBox.Text = css;
                    JsBox.Text = js;
                    // Sync preview size to manifest
                    PreviewWidthSlider.Value = Math.Clamp(manifest.Width ?? 300, 150, 600);
                    PreviewHeightSlider.Value = Math.Clamp(manifest.Height ?? 220, 150, 600);
                    PreviewFrame.Width = PreviewWidthSlider.Value;
                    PreviewFrame.Height = PreviewHeightSlider.Value;
                    UpdatePreviewSizeLabel();
                }
                finally { _isUpdatingFields = false; }

                UpdateManifestBox();
                RefreshPreview();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load folder:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveToFolder_Click(object sender, RoutedEventArgs e)
        {
            string? targetFolder = _currentFolder;
            if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Select or create folder to save widget"
                };
                if (dlg.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dlg.FolderName)) return;
                targetFolder = dlg.FolderName;
                // If user picks an empty new folder, use it directly; if they pick existing widget folder, confirm
                if (!Directory.Exists(targetFolder)) Directory.CreateDirectory(targetFolder);
            }

            SaveToFolder(targetFolder);
        }

        private void SaveToFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var manifest = BuildManifestFromFields();
                // Sanitize folder name matches manifest Name if user changed name and is saving to new location
                // Keep folder as-is, but ensure manifest Name is set

                var html = HtmlBox.Text ?? "";
                var css = CssBox.Text ?? "";
                var js = JsBox.Text ?? "";
                var json = JsonSerializer.Serialize(manifest, JsonOpts);

                File.WriteAllText(Path.Combine(folder, "widget.json"), json, Encoding.UTF8);
                File.WriteAllText(Path.Combine(folder, "index.html"), html, Encoding.UTF8);
                File.WriteAllText(Path.Combine(folder, "style.css"), css, Encoding.UTF8);
                File.WriteAllText(Path.Combine(folder, "script.js"), js, Encoding.UTF8);

                _currentFolder = folder;
                CurrentFolderLabel.Text = folder;
                UpdateManifestBox();

                // Also update preview to reflect saved manifest
                RefreshPreview();

                MessageBox.Show($"Widget saved to:\n{folder}", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenFolderInExplorer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? folder = _currentFolder;
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    // Try default UserWidgets root
                    var userRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopBoxes", "UserWidgets");
                    folder = Directory.Exists(userRoot) ? userRoot : null;
                }
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _debounce.Stop(); } catch { }
            try { PreviewControl.CleanupForShutdown(); } catch { }
            base.OnClosed(e);
        }
    }
}
