using System.Runtime.Versioning;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Win32.NativeMethods;
using HWND = Windows.Win32.Foundation.HWND;

namespace DesktopBoxesUI.Win32.Services;

/// <summary>
/// Win32 implementation of <see cref="IExplorerDesktopService"/>. Locates the Explorer desktop
/// list-view (Progman / WorkerW -&gt; SHELLDLL_DefView -&gt; SysListView32) and hides or shows its
/// icons, and re-hides them automatically when Explorer restarts.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class ExplorerDesktopService : IExplorerDesktopService
{
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const uint EventObjectCreate = 0x8000;
    private const uint WineventOutOfContext = 0x0002;
    private const uint WineventSkipOwnProcess = 0x0008;

    private bool _hidden;
    private IntPtr _hook;
    private WinEventProc? _hookProc;

    public bool AreDesktopIconsVisible => !_hidden;

    public void SetDesktopIconsVisible(bool visible)
    {
        var listView = FindDesktopListView();
        if (listView != IntPtr.Zero)
        {
            Win32Apis.ShowWindow((HWND)listView, visible ? SwShow : SwHide);
        }

        _hidden = !visible;

        if (_hidden && _hook == IntPtr.Zero)
        {
            InstallRestartHook();
        }
        else if (!_hidden && _hook != IntPtr.Zero)
        {
            Win32Apis.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
            _hookProc = null;
        }
    }

    public event EventHandler? ExplorerRestarted;

    private void InstallRestartHook()
    {
        _hookProc = OnWinEvent;
        _hook = Win32Apis.SetWinEventHook(
            EventObjectCreate,
            EventObjectCreate,
            IntPtr.Zero,
            _hookProc,
            0,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);
    }

    private void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        if (_hidden)
        {
            var listView = FindDesktopListView();
            if (listView != IntPtr.Zero)
            {
                Win32Apis.ShowWindow((HWND)listView, SwHide);
            }

            ExplorerRestarted?.Invoke(this, EventArgs.Empty);
        }
    }

    internal static IntPtr FindDesktopListView()
    {
        HWND progman = Win32Apis.FindWindowEx(HWND.Null, HWND.Null, "Progman", null);
        HWND defView = Win32Apis.FindWindowEx(progman, HWND.Null, "SHELLDLL_DefView", null);
        HWND listView = Win32Apis.FindWindowEx(defView, HWND.Null, "SysListView32", null);

        if ((IntPtr)listView == IntPtr.Zero)
        {
            HWND worker = Win32Apis.FindWindowEx(HWND.Null, HWND.Null, "WorkerW", null);
            while ((IntPtr)worker != IntPtr.Zero)
            {
                defView = Win32Apis.FindWindowEx(worker, HWND.Null, "SHELLDLL_DefView", null);
                listView = Win32Apis.FindWindowEx(defView, HWND.Null, "SysListView32", null);
                if ((IntPtr)listView != IntPtr.Zero)
                {
                    break;
                }

                worker = Win32Apis.FindWindowEx(HWND.Null, worker, "WorkerW", null);
            }
        }

        return (IntPtr)listView;
    }
}
