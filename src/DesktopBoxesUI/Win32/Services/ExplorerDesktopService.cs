using System;
using System.Runtime.Versioning;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Win32.NativeMethods;
using HWND = Windows.Win32.Foundation.HWND;

namespace DesktopBoxesUI.Win32.Services;

/// <summary>
/// Win32 implementation of <see cref="IExplorerDesktopService"/>. Hides or shows the Explorer
/// desktop icons by toggling the desktop's <c>SHELLDLL_DefView</c> via the same <c>WM_COMMAND</c>
/// Explorer's own "Show desktop icons" menu uses. This hides the icon container while keeping the
/// desktop view alive, so the right-click "New" verb keeps working — unlike hiding the list-view
/// window directly, which breaks that verb with error 16389.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class ExplorerDesktopService : IExplorerDesktopService
{
    private bool? _lastApplied;

    public bool AreDesktopIconsVisible => !_lastApplied ?? true;

    public void SetDesktopIconsVisible(bool visible)
    {
        bool hide = !visible;

        IntPtr listView = FindDesktopListView();
        bool currentlyHidden = listView != IntPtr.Zero && !Win32Apis.IsWindowVisible(listView);
        if (currentlyHidden == hide)
        {
            _lastApplied = hide;
            return;
        }

        _lastApplied = hide;
        Win32Apis.ToggleDesktopIcons();
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
