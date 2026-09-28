using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Maktaby.Core.Interfaces;
using Maktaby.Win32.NativeMethods;
using Maktaby.Native;
using static Maktaby.Native.Win32Constants;

namespace Maktaby.Win32.Services;

/// <summary>
/// Installs a low-level (WH_MOUSE_LL) mouse hook on a dedicated background thread (with its own
/// message pump) and raises <see cref="MouseButtonDown"/> with the window under the cursor for every
/// mouse-button press. Running the hook on its own thread keeps it independent of the WPF UI thread,
/// so a busy/saturated UI thread can never delay mouse input delivered to other applications. The
/// raised event is marshaled back to the UI thread before touching any WPF state.
/// </summary>
internal sealed class MouseMonitor : IMouseMonitor, IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    private readonly HookProc _proc;
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private IntPtr _hook;

    public event EventHandler<IntPtr>? MouseButtonDown;

    /// <summary>Raised when a double left-click on empty desktop area is detected (see <see cref="TryDetectDesktopDoubleClick"/>).</summary>
    public event EventHandler? DesktopDoubleClick;

    // Double-click tracking (single hook thread, so no synchronization needed).
    private int _lastClickTime;
    private int _lastClickX;
    private int _lastClickY;
    private IntPtr _lastClickHwnd;
    private readonly int _doubleClickTime;
    private readonly int _doubleClickX;
    private readonly int _doubleClickY;

    public MouseMonitor()
    {
        _proc = HookCallback;
        _doubleClickTime = User32.GetDoubleClickTime();
        _doubleClickX = User32.GetSystemMetrics(SM_CXDOUBLECLK);
        _doubleClickY = User32.GetSystemMetrics(SM_CYDOUBLECLK);
    }

    public void Start()
    {
        if (_thread != null)
        {
            return;
        }

        _thread = new Thread(ThreadProc)
        {
            IsBackground = true,
            Name = "Maktaby.MouseMonitor",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            Win32Apis.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }

        // Shut the dedicated message loop down; the thread then exits on its own.
        _dispatcher?.InvokeShutdown();

        try
        {
            _thread?.Join(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            // best-effort; the thread is a background thread and will not block process exit
        }

        _thread = null;
        _dispatcher = null;
    }

    private void ThreadProc()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;

        var module = Win32Apis.GetModuleHandle(null);
        _hook = Win32Apis.SetWindowsHookEx(Win32Apis.WH_MOUSE_LL, _proc, module, 0);

        // Pump messages on this dedicated thread. The OS invokes the hook callback here, so the hook
        // is fully decoupled from the WPF UI thread.
        Dispatcher.Run();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is
                WM_LBUTTONDOWN or
                WM_RBUTTONDOWN or
                WM_MBUTTONDOWN or
                WM_XBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                var hwnd = Win32Apis.WindowFromPoint(info.pt);
                var handler = MouseButtonDown;
                if (handler != null && Application.Current != null)
                {
                    // Defer WPF work to the UI thread so the hook thread never blocks on it.
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => handler(this, hwnd)));
                }

                if (msg == WM_LBUTTONDOWN && Application.Current != null)
                {
                    TryDetectDesktopDoubleClick(info.pt, hwnd);
                    // TODO: Revisit whether a global WH_MOUSE_LL hook observing every click is acceptable
                    // for other apps/games. Disabled pending that review.
                    // MaybeDefocusOnDesktopClick(hwnd);
                }
            }
        }

        return Win32Apis.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Recognizes a double left-click on empty desktop area (so a desktop icon double-click still
    /// opens the icon) and raises <see cref="DesktopDoubleClick"/> on the UI thread. Only left-button
    /// downs whose previous click landed on the same desktop list-view within the OS double-click
    /// time/distance count; an <c>LVM_HITTEST</c> then confirms the point is not on an icon.
    /// </summary>
    private void TryDetectDesktopDoubleClick(POINT pt, IntPtr hwnd)
    {
        bool isDouble = false;
        if (_lastClickHwnd == hwnd && _lastClickTime != 0)
        {
            int now = Environment.TickCount;
            int dt = unchecked(now - _lastClickTime);
            int dx = pt.X - _lastClickX;
            int dy = pt.Y - _lastClickY;
            if (dt >= 0 && dt <= _doubleClickTime &&
                Math.Abs(dx) <= _doubleClickX && Math.Abs(dy) <= _doubleClickY)
            {
                isDouble = true;
            }
        }

        // Record this click as the baseline for the next potential double-click.
        _lastClickTime = Environment.TickCount;
        _lastClickX = pt.X;
        _lastClickY = pt.Y;
        _lastClickHwnd = hwnd;

        if (isDouble && Win32Apis.IsDesktopChild(hwnd) && !Win32Apis.IsBoxWindow(hwnd))
        {
            // hwnd is the Explorer desktop (list-view, or our transparent surface when icons are hidden).
            bool isListView = Win32Apis.IsSysListView32(hwnd);
            var handler = DesktopDoubleClick;
            if (handler != null && Application.Current != null)
            {
                // Defer the (potentially blocking) ListView hit-test and the event to the UI thread so
                // the low-level hook thread never stalls on a cross-process SendMessage.
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    // For the Explorer list-view only, confirm the point is not on an icon (so an icon
                    // double-click still opens it). The transparent surface is always empty.
                    if (!isListView || Win32Apis.IsDesktopEmptyPoint(hwnd, pt))
                    {
                        handler(this, EventArgs.Empty);
                    }
                }));
            }
        }
    }

    /// <summary>
    /// When a single left-click lands on the desktop while one of our box windows is foreground, move
    /// focus to the desktop shell window so the box is deactivated (un-focused). We only act when our
    /// own box is foreground, never stealing focus from another application.
    /// </summary>
    private void MaybeDefocusOnDesktopClick(IntPtr hwnd)
    {
        if (!Win32Apis.IsDesktopChild(hwnd) || Win32Apis.IsBoxWindow(hwnd))
        {
            return;
        }

        IntPtr foreground = Win32Apis.GetForegroundWindow();
        if (!Win32Apis.IsBoxWindow(foreground))
        {
            return;
        }

        IntPtr shell = Win32Apis.GetShellWindow();
        if (shell != IntPtr.Zero)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() => Win32Apis.SetForegroundWindow(shell)));
        }
    }

    public void Dispose() => Stop();
}
