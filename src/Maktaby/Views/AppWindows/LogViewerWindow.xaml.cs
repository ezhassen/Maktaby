using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Maktaby.Views;

/// <summary>Read-only log viewer (AvalonEdit): picks any available daily log file (including
/// same-day size-roll chunks), shows the tail of huge files, and optionally follows the live
/// file. Opened from Settings → General, next to the logging level.</summary>
public partial class LogViewerWindow : AppWindows.AppFluentWindow
{
    /// <summary>Initial load reads at most this much (from the end); the live tail keeps
    /// appending after that, capped by <see cref="MaxDocumentBytes"/>.</summary>
    private const long MaxInitialBytes = 2L * 1024 * 1024;
    private const int MaxDocumentBytes = 3 * 1024 * 1024;
    private const int TrimDocumentBytes = 1 * 1024 * 1024;

    private readonly DispatcherTimer _tailTimer;
    private string? _currentPath;
    private long _currentLength;

    public LogViewerWindow()
    {
        InitializeComponent();
        _tailTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tailTimer.Tick += (_, _) => TailTick();
        Loaded += (_, _) =>
        {
            RefreshFileList(selectNewest: true);
            _tailTimer.Start();
        };
        Closed += (_, _) =>
        {
            try { _tailTimer.Stop(); } catch { }
        };
    }

    private static string LogsFolder
    {
        get
        {
            try { return Logging.LogsFolder; }
            catch { return Path.Combine(Path.GetTempPath(), "Maktaby\\"); }
        }
    }

    private void RefreshFileList(bool selectNewest)
    {
        List<FileInfo> files = new();
        try
        {
            var dir = new DirectoryInfo(LogsFolder);
            if (dir.Exists)
            {
                files = dir.GetFiles("Maktaby*.log")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ThenByDescending(f => f.Name, StringComparer.Ordinal)
                    .ToList();
            }
        }
        catch { }
        try
        {
            // Items are FileInfo (displayed by name via DisplayMemberPath); compare by full path.
            var fresh = files.ToList();
            var current = FilePicker.ItemsSource as System.Collections.IList;
            bool same = false;
            if (current is not null && current.Count == fresh.Count)
            {
                same = true;
                for (int i = 0; i < fresh.Count; i++)
                {
                    if (!string.Equals((current[i] as FileInfo)?.FullName, fresh[i].FullName, StringComparison.Ordinal)) { same = false; break; }
                }
            }
            if (same)
            {
                UpdateStatus();
                return;
            }
            string? selected = (FilePicker.SelectedItem as FileInfo)?.FullName;
            FilePicker.ItemsSource = fresh;
            if (selectNewest && fresh.Count > 0)
            {
                FilePicker.SelectedIndex = 0;
            }
            else if (selected is not null)
            {
                int ix = fresh.FindIndex(f => string.Equals(f.FullName, selected, StringComparison.Ordinal));
                FilePicker.SelectedIndex = ix;
            }
            UpdateStatus();
        }
        catch { }
    }

    private void FilePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        LoadSelected();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        RefreshFileList(selectNewest: false);
        LoadSelected();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Logging.OpenLogsFolderInExplorer(); } catch { }
    }

    private void LoadSelected()
    {
        var path = (FilePicker.SelectedItem as FileInfo)?.FullName;
        if (string.IsNullOrEmpty(path))
        {
            _currentPath = null;
            try { LogBox.Text = ""; } catch { }
            UpdateStatus();
            return;
        }
        LoadFile(path);
    }

    private void LoadFile(string path)
    {
        _currentPath = path;
        _currentLength = 0;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                LogBox.Text = $"File no longer exists:{Environment.NewLine}{path}";
                UpdateStatus();
                return;
            }
            long start = Math.Max(0, info.Length - MaxInitialBytes);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            string text = reader.ReadToEnd();
            if (start > 0)
            {
                // Skip the partial first line, then mark the truncation.
                int nl = text.IndexOf('\n');
                if (nl >= 0) text = text[(nl + 1)..];
                text = $"[… showing last {FormatBytes(info.Length - start)} of {FormatBytes(info.Length)} …]{Environment.NewLine}{Environment.NewLine}{text}";
            }
            LogBox.Text = text;
            _currentLength = info.Length;
            if (FollowTailCheck.IsChecked == true) LogBox.ScrollToEnd();
            else LogBox.ScrollToHome();
        }
        catch (Exception ex)
        {
            try { LogBox.Text = $"Could not read log file:{Environment.NewLine}{ex.Message}"; } catch { }
        }
        UpdateStatus();
    }

    private void TailTick()
    {
        try
        {
            // Pick up same-day size rolls (_001, …) while following the newest file.
            bool followingNewest = FollowTailCheck.IsChecked == true
                && FilePicker.SelectedIndex <= 0;
            RefreshFileList(selectNewest: followingNewest);
            if (FollowTailCheck.IsChecked != true) return;
            if (string.IsNullOrEmpty(_currentPath)) return;
            var info = new FileInfo(_currentPath);
            if (!info.Exists) return;
            if (info.Length < _currentLength)
            {
                // Rotated/truncated underneath us: reload from scratch.
                LoadFile(_currentPath);
                return;
            }
            if (info.Length == _currentLength) return;
            string delta;
            using (var stream = new FileStream(_currentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(_currentLength, SeekOrigin.Begin);
                using var reader = new StreamReader(stream);
                delta = reader.ReadToEnd();
            }
            _currentLength = info.Length;
            if (string.IsNullOrEmpty(delta)) return;
            try
            {
                var doc = LogBox.Document;
                if (doc.TextLength + delta.Length > MaxDocumentBytes)
                {
                    int drop = Math.Min(doc.TextLength, TrimDocumentBytes);
                    doc.Remove(0, drop);
                }
                doc.Insert(doc.TextLength, delta);
                LogBox.ScrollToEnd();
            }
            catch { }
            UpdateStatus();
        }
        catch { }
    }

    private void UpdateStatus()
    {
        try
        {
            if (string.IsNullOrEmpty(_currentPath))
            {
                StatusText.Text = "No log file loaded";
                return;
            }
            long len;
            try { len = new FileInfo(_currentPath).Length; }
            catch { len = _currentLength; }
            int lines;
            try { lines = LogBox.Document.LineCount; } catch { lines = 0; }
            StatusText.Text = $"{Path.GetFileName(_currentPath)} · {FormatBytes(len)} on disk · {lines:N0} lines shown"
                + (FollowTailCheck.IsChecked == true ? " · following" : "");
        }
        catch { }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0} KB";
        return $"{bytes / 1024.0 / 1024.0:0.0} MB";
    }
}
