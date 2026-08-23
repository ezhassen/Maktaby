using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Win32.NativeMethods;

namespace DesktopBoxesUI.Win32.Services;

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
        public ManualApis.POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    private readonly ManualApis.HookProc _proc;
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private IntPtr _hook;

    public event EventHandler<IntPtr>? MouseButtonDown;

    public MouseMonitor()
    {
        _proc = HookCallback;
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
            Name = "DesktopBoxes.MouseMonitor",
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
                ManualApis.WM_LBUTTONDOWN or
                ManualApis.WM_RBUTTONDOWN or
                ManualApis.WM_MBUTTONDOWN or
                ManualApis.WM_XBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                var hwnd = Win32Apis.WindowFromPoint(info.pt);
                var handler = MouseButtonDown;
                if (handler != null && Application.Current != null)
                {
                    // Defer WPF work to the UI thread so the hook thread never blocks on it.
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => handler(this, hwnd)));
                }
            }
        }

        return Win32Apis.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
