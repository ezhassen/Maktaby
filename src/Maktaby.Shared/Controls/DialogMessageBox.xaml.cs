using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Maktaby.Native;
using Wpf.Ui.Controls;
using Maktaby.Shared.Interfaces;
using Maktaby.Shared.Win32APIs;

namespace Maktaby.Shared.Controls;

/// <summary>
/// Custom modal message box that replaces WPF-UI's in-window <see cref="ContentDialog"/>.
/// It is a <see cref="Wpf.Ui.Controls.FluentWindow"/> shown with <see cref="Window.ShowDialog"/> so it
/// blocks its owner, is centered over the owner and clamped to the same monitor's work area.
/// Size is calculated manually to autosize to content within min/max while keeping the footer
/// docked at the bottom with no gap and preserving the themed FluentWindow title bar.
/// </summary>
public partial class DialogMessageBox : Wpf.Ui.Controls.FluentWindow
{
    public ContentDialogResult Result { get; private set; } = ContentDialogResult.None;

    private readonly DialogOptions _options;
    private HwndSource? _hwndSource;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var src = HwndSource.FromHwnd(hwnd);
                src?.AddHook(HwndHook);
                _hwndSource = src;
            }
        }
        catch { }
    }

    public DialogMessageBox(Window? owner, DialogOptions options)
    {
        _options = options;
        InitializeComponent();

        // Window chrome - FluentWindow title bar follows theme
        Title = string.IsNullOrEmpty(options.Title) ? "Message" : options.Title;
        TitleBar.Title = Title;

        if (owner != null)
        {
            Owner = owner;
        }

        // Content: string => TextBlock (wrapping). MaxWidth is clamped to window MaxWidth
        // minus horizontal padding (24+24) so SizeToContent=WidthAndHeight can wrap instead
        // of measuring as a single unwrapped line. Keeps short messages narrow but limited by MinWidth.
        const double maxContentWidth = 432; // 480 (window MaxWidth) - 48 (ScrollViewer padding)
        if (options.Content is string text)
        {
            ContentHost.Content = new System.Windows.Controls.TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = maxContentWidth,
                Foreground = (Brush)FindResource("TextFillColorPrimaryBrush")
            };
        }
        else if (options.Content is UIElement element)
        {
            // Constrain wide UIElement content so the window autosize stays within MaxWidth
            if (element is FrameworkElement fe && double.IsNaN(fe.MaxWidth))
            {
                fe.MaxWidth = maxContentWidth;
            }

            ContentHost.Content = element;
        }
        else if (options.Content != null)
        {
            ContentHost.Content = new System.Windows.Controls.TextBlock
            {
                Text = options.Content.ToString(),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = maxContentWidth,
                Foreground = (Brush)FindResource("TextFillColorPrimaryBrush")
            };
        }
        else
        {
            ContentHost.Content = null;
        }

        // Buttons
        ConfigureButton(PrimaryButton, options.PrimaryButtonText, options.PrimaryButtonAppearance ?? ControlAppearance.Primary);
        ConfigureButton(SecondaryButton, options.SecondaryButtonText, options.SecondaryButtonAppearance ?? ControlAppearance.Secondary);
        ConfigureButton(CloseButton, options.CloseButtonText, options.CloseButtonAppearance ?? ControlAppearance.Secondary);

        // If only Close was requested but no explicit text, ensure we still show it
        // (DialogService already merges CloseButtonText = "OK" / "Cancel" for ShowMessage/ShowConfirm)
        // Buttons with empty text are collapsed so no empty button renders.

        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnClosing;
    }

    private static void ConfigureButton(Wpf.Ui.Controls.Button button, string? text, ControlAppearance appearance)
    {
        if (string.IsNullOrEmpty(text))
        {
            button.Visibility = Visibility.Collapsed;
            button.Content = string.Empty;
            return;
        }

        button.Visibility = Visibility.Visible;
        button.Content = text;
        button.Appearance = appearance;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Manually autosize before clearing resize style - style change would affect DesiredSize.
        ApplyManualSize();

        // Default button focus
        try
        {
            Wpf.Ui.Controls.Button? defaultBtn = _options.DefaultButton switch
            {
                ContentDialogButton.Primary when PrimaryButton.Visibility == Visibility.Visible => PrimaryButton,
                ContentDialogButton.Secondary when SecondaryButton.Visibility == Visibility.Visible => SecondaryButton,
                ContentDialogButton.Close when CloseButton.Visibility == Visibility.Visible => CloseButton,
                _ => null
            };

            // Fallback: focus the most affirmative visible button
            defaultBtn ??= PrimaryButton.Visibility == Visibility.Visible ? PrimaryButton
                : SecondaryButton.Visibility == Visibility.Visible ? SecondaryButton
                : CloseButton.Visibility == Visibility.Visible ? CloseButton
                : null;

            defaultBtn?.Focus();
            if (defaultBtn != null)
            {
                defaultBtn.IsDefault = true;
            }
        }
        catch { }

        PositionCenterParent();

        // Now clear resize style (after measure) and force non-client recalc
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                const int WS_THICKFRAME = 0x00040000;
                const int WS_MAXIMIZEBOX = 0x00010000;
                const int SWP_NOMOVE = 0x0002;
                const int SWP_NOSIZE = 0x0001;
                const int SWP_NOZORDER = 0x0004;
                const int SWP_FRAMECHANGED = 0x0020;
                int style = User32.GetWindowLong(hwnd, Win32Constants.GWL_STYLE);
                style &= ~WS_THICKFRAME;
                style &= ~WS_MAXIMIZEBOX;
                User32.SetWindowLong(hwnd, Win32Constants.GWL_STYLE, style);
                User32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
                var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
                if (chrome != null)
                {
                    chrome.ResizeBorderThickness = new Thickness(0);
                }
                else
                {
                    // FluentWindow uses its own chrome; create one that disables resize border
                    var newChrome = new System.Windows.Shell.WindowChrome
                    {
                        ResizeBorderThickness = new Thickness(0),
                        CaptionHeight = 32,
                        CornerRadius = new CornerRadius(8),
                        GlassFrameThickness = new Thickness(-1)
                    };
                    System.Windows.Shell.WindowChrome.SetWindowChrome(this, newChrome);
                }
            }
        }
        catch { }
    }
    protected override void OnClosed(EventArgs e)
    {
        _hwndSource?.RemoveHook(HwndHook);
        _hwndSource = null;
        base.OnClosed(e);
    }
    private const int WmNcHitTest = 0x0084;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmWindowPosChanging = 0x0046;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (ResizeMode != ResizeMode.NoResize) return IntPtr.Zero;

        if (msg == WmNcHitTest)
        {
            var hit = User32.DefWindowProc(hwnd, (uint)msg, wParam, lParam).ToInt32();
            if (hit >= 10 && hit <= 17)
            {
                handled = true;
                return new IntPtr(1);
            }
        }
        else if (msg == WmGetMinMaxInfo)
        {
            try
            {
                var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
                // Lock to current outer size (in pixels)
                var dpi = VisualTreeHelper.GetDpi(this);
                int curW = (int)Math.Round(Width * dpi.DpiScaleX);
                int curH = (int)Math.Round(Height * dpi.DpiScaleY);
                // Ensure at least MinWidth/MinHeight in pixels
                int minW = (int)Math.Round(MinWidth * dpi.DpiScaleX);
                int minH = (int)Math.Round(MinHeight * dpi.DpiScaleY);
                if (curW < minW) curW = minW;
                if (curH < minH) curH = minH;
                mmi.ptMinTrackSize.x = curW;
                mmi.ptMinTrackSize.y = curH;
                mmi.ptMaxTrackSize.x = curW;
                mmi.ptMaxTrackSize.y = curH;
                System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
                handled = true;
                return IntPtr.Zero;
            }
            catch { }
        }
        else if (msg == WmWindowPosChanging)
        {
            try
            {
                var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(lParam);
                const uint SWP_NOSIZE = 0x0001;
                if ((pos.flags & SWP_NOSIZE) == 0)
                {
                    // Prevent any size change - keep current size
                    var dpi = VisualTreeHelper.GetDpi(this);
                    int curW = (int)Math.Round(Width * dpi.DpiScaleX);
                    int curH = (int)Math.Round(Height * dpi.DpiScaleY);
                    // Allow only if new size equals current (within 2px for rounding)
                    int newW = pos.cx;
                    int newH = pos.cy;
                    if (Math.Abs(newW - curW) > 2 || Math.Abs(newH - curH) > 2)
                    {
                        pos.cx = curW;
                        pos.cy = curH;
                        System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lParam, true);
                        handled = true;
                        return IntPtr.Zero;
                    }
                }
            }
            catch { }
        }
        return IntPtr.Zero;
    }

    private void ApplyManualSize()
    {
        try
        {
            // Window min/max are the source of truth for autosize
            double minW = MinWidth;
            double maxW = MaxWidth;
            double minH = MinHeight;
            double maxH = MaxHeight;

            if (double.IsNaN(minW) || minW <= 0) minW = 340;
            if (double.IsNaN(maxW) || maxW <= 0) maxW = 480;
            if (double.IsNaN(minH) || minH <= 0) minH = 140;
            if (double.IsNaN(maxH) || maxH <= 0) maxH = 640;

            // Ensure layout is up to date and capture chrome size
            RootPanel.UpdateLayout();
            double chromeW = 0, chromeH = 0;
            try
            {
                chromeW = Math.Max(0, ActualWidth - RootPanel.ActualWidth);
                chromeH = Math.Max(0, ActualHeight - RootPanel.ActualHeight);
            }
            catch { }

            System.Windows.Controls.TextBlock? tb = ContentHost.Content as System.Windows.Controls.TextBlock;
            double savedMax = double.NaN;
            bool hasTb = tb != null;
            double tbNaturalW = 0, tbNaturalH = 0, tbWrappedH = 0;
            double titleW = 0, footerW = 0;
            TitleBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            titleW = TitleBar.DesiredSize.Width;
            FooterBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            footerW = FooterBorder.DesiredSize.Width;
            double contentPad = 48;
            double naturalContentW;
            if (hasTb)
            {
                savedMax = tb!.MaxWidth;
                // Use FormattedText for true single-line natural width (independent of layout constraints)
                try
                {
                    var dpi = VisualTreeHelper.GetDpi(tb);
                    var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
                    var ft = new FormattedText(tb.Text ?? string.Empty, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, tb.FontSize, Brushes.Black, dpi.PixelsPerDip);
                    tbNaturalW = ft.Width;
                    tbNaturalH = ft.Height;
                }
                catch
                {
                    tb.MaxWidth = double.PositiveInfinity;
                    tb.InvalidateMeasure();
                    tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    tbNaturalW = tb.DesiredSize.Width;
                    tbNaturalH = tb.DesiredSize.Height;
                    tb.MaxWidth = double.IsNaN(savedMax) ? 432 : savedMax;
                    tb.InvalidateMeasure();
                }
                // For wrapped height at 432
                tb!.MaxWidth = double.IsNaN(savedMax) ? 432 : savedMax;
                tb.InvalidateMeasure();
                tb.Measure(new Size(432, double.PositiveInfinity));
                tbWrappedH = tb.DesiredSize.Height;
                // Restore infinity for natural width already captured, now keep capped for further measures
                double contentNeeded = tbNaturalW + contentPad + 16; // +16 for TextBlock margin/border
                naturalContentW = Math.Max(contentNeeded, Math.Max(titleW, footerW));
                // Ensure at least footer/title width
                if (naturalContentW < footerW) naturalContentW = footerW;
                if (naturalContentW < titleW) naturalContentW = titleW;
            }
            else
            {
                RootPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                naturalContentW = RootPanel.DesiredSize.Width;
            }


            // Measure at max width for wrapped height
            RootPanel.InvalidateMeasure();
            if (hasTb) tb!.InvalidateMeasure();
            RootPanel.Measure(new Size(maxW, double.PositiveInfinity));
            Size atMaxW = RootPanel.DesiredSize;

            // Also get unwrapped root size for logging
            RootPanel.InvalidateMeasure();
            if (hasTb)
            {
                tb!.MaxWidth = double.PositiveInfinity;
                tb.InvalidateMeasure();
                RootPanel.InvalidateMeasure();
            }
            RootPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size natural = RootPanel.DesiredSize;
            if (hasTb)
            {
                tb!.MaxWidth = double.IsNaN(savedMax) ? 432 : savedMax;
                tb.InvalidateMeasure();
                RootPanel.InvalidateMeasure();
            }

            double targetW;
            double targetH;

            if (naturalContentW < maxW - 1)
            {
                targetW = Math.Clamp(naturalContentW, minW, maxW);
                RootPanel.Measure(new Size(targetW, double.PositiveInfinity));
                Size atTargetW = RootPanel.DesiredSize;
                targetH = Math.Clamp(atTargetW.Height, minH, maxH);
                // If height at narrow width is much larger than at max, prefer max to reduce height
                double heightAtMax = atMaxW.Height;
                if (atTargetW.Height > heightAtMax + 24)
                {
                    targetW = maxW;
                    targetH = Math.Clamp(heightAtMax, minH, maxH);
                }
            }
            else
            {
                targetW = maxW;
                targetH = Math.Clamp(atMaxW.Height, minH, maxH);
            }

            double outerW = targetW + chromeW;
            double outerH = targetH + chromeH;
            outerW = Math.Clamp(outerW, minW, maxW);
            outerH = Math.Clamp(outerH, minH, maxH);

            Width = outerW;
            Height = outerH;
            UpdateLayout();

            Size unwrapped = natural;

            /*// Debug trace (leave for diagnosis)
            try
            {
                var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dbx_dialog_size.log");
                System.IO.File.AppendAllText(log,
                    $"{DateTime.Now:HH:mm:ss.fff} title='{Title}' unwrapped={unwrapped.Width:F0}x{unwrapped.Height:F0} atMax={atMaxW.Width:F0}x{atMaxW.Height:F0} targetClient={targetW:F0}x{targetH:F0} chrome={chromeW:F0}x{chromeH:F0} outer={outerW:F0}x{outerH:F0} content='{(_options.Content?.ToString()?.Length > 80 ? _options.Content.ToString()!.Substring(0, 80) : _options.Content?.ToString())}'{Environment.NewLine}");
            }
            catch { }*/
        }
        catch
        {
            // Best effort - fall back to WPF default sizing
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Esc => Close (None) unless Close is hidden, then treat as cancel
            Result = ContentDialogResult.None;
            Close();
            e.Handled = true;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // X button or Alt+F4 => None (already default)
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        Result = ContentDialogResult.Primary;
        Close();
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        Result = ContentDialogResult.Secondary;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Result = ContentDialogResult.None;
        Close();
    }

    /// <summary>
    /// Centers the dialog over its <see cref="Window.Owner"/> (or primary screen if no owner) and
    /// clamps it so the entire window stays within that monitor's work area (taskbar excluded).
    /// Uses physical monitor work area converted to DIPs for per-monitor DPI correctness.
    /// </summary>
    private void PositionCenterParent()
    {
        try
        {
            double w = ActualWidth;
            if (double.IsNaN(w) || w <= 0) w = Width;
            if (double.IsNaN(w) || w <= 0) w = 420;

            double h = ActualHeight;
            if (double.IsNaN(h) || h <= 0) h = ActualHeight;
            if (double.IsNaN(h) || h <= 0) h = 200;

            // Resolve work area in DIPs for the owner's monitor
            Rect workArea = GetWorkAreaDips();

            double left;
            double top;

            if (Owner != null)
            {
                // Owner rect is already in DIPs (WPF coordinates)
                double ownerLeft = Owner.Left;
                double ownerTop = Owner.Top;
                double ownerWidth = Owner.Width;
                double ownerHeight = Owner.Height;

                // If owner hasn't been shown or has NaN, fall back to workArea center
                if (double.IsNaN(ownerLeft) || double.IsNaN(ownerTop) || ownerWidth <= 0 || ownerHeight <= 0)
                {
                    left = workArea.Left + (workArea.Width - w) / 2;
                    top = workArea.Top + (workArea.Height - h) / 2;
                }
                else
                {
                    left = ownerLeft + (ownerWidth - w) / 2;
                    top = ownerTop + (ownerHeight - h) / 2;
                }
            }
            else
            {
                left = workArea.Left + (workArea.Width - w) / 2;
                top = workArea.Top + (workArea.Height - h) / 2;
            }

            // Clamp within work area
            if (left < workArea.Left) left = workArea.Left;
            if (top < workArea.Top) top = workArea.Top;
            if (left + w > workArea.Right) left = workArea.Right - w;
            if (top + h > workArea.Bottom) top = workArea.Bottom - h;

            // Final safety clamp
            if (left < workArea.Left) left = workArea.Left;
            if (top < workArea.Top) top = workArea.Top;

            Left = left;
            Top = top;
        }
        catch
        {
            // Best-effort positioning; fall back to WPF default if anything fails
        }
    }

    private Rect GetWorkAreaDips()
    {
        try
        {
            Window? owner = Owner;
            if (owner != null)
            {
                var hwnd = new WindowInteropHelper(owner).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    var wa = Win32Apis.GetMonitorWorkArea(hwnd);
                    if (wa is { } tuple)
                    {
                        // Convert physical pixels -> DIPs using WPF's transform
                        var source = PresentationSource.FromVisual(owner);
                        if (source?.CompositionTarget != null)
                        {
                            var fromDevice = source.CompositionTarget.TransformFromDevice;
                            var tl = fromDevice.Transform(new Point(tuple.left, tuple.top));
                            var br = fromDevice.Transform(new Point(tuple.right, tuple.bottom));
                            return new Rect(tl, br);
                        }

                        // Fallback: GetDpiForWindow conversion
                        try
                        {
                            uint dpi = Win32Apis.GetDpiForWindow(hwnd);
                            if (dpi != 0)
                            {
                                double scale = dpi / 96.0;
                                return new Rect(
                                    tuple.left / scale,
                                    tuple.top / scale,
                                    (tuple.right - tuple.left) / scale,
                                    (tuple.bottom - tuple.top) / scale);
                            }
                        }
                        catch { }
                    }
                }

                // Fallback: owner is on a monitor, use its Left/Top to guess work area via SystemParameters when Win32 fails
            }
        }
        catch { }

        return SystemParameters.WorkArea;
    }
}
