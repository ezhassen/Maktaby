using Maktaby.Core.Interfaces;
using Maktaby.Shell.Interop;
using Maktaby.Win32.NativeMethods;
using System;
using System.Runtime.Versioning;
using Maktaby.Native;

namespace Maktaby.Win32.Services;

/// <summary>
/// Win32 implementation of <see cref="IExplorerDesktopService"/>. Hides or shows the Explorer
/// desktop icons by hiding the desktop's <c>SHELLDLL_DefView</c>'s list-view, keeping the desktop
/// view itself alive so the right-click "New" verb and the app's own input forwarding keep
/// working.
/// </summary>
/// <remarks>
/// It used to send the shell's "toggle desktop icons" command instead, but that flips a state the
/// shell keeps globally: the desktop's icons went away and so did the icons in every open Explorer
/// window. Hiding the one list-view touches nothing else.
/// </remarks>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class ExplorerDesktopService : IExplorerDesktopService
{
    private bool? _lastApplied;

    public bool AreDesktopIconsVisible => !_lastApplied ?? true;

    public void SetDesktopIconsVisible(bool visible)
    {
        bool hide = !visible;

        IntPtr listView = FindDesktopListView();
        if (listView == IntPtr.Zero)
        {
            // Explorer has no desktop view yet (it is still starting). The shell's own toggle is the
            // only thing that can work before the list-view exists.
            _lastApplied = hide;
            Win32Apis.ToggleDesktopIcons();
            return;
        }

        bool currentlyHidden = !Win32Apis.IsWindowVisible(listView);
        if (currentlyHidden == hide)
        {
            _lastApplied = hide;
            return;
        }

        _lastApplied = hide;

        // Hide the desktop's icon list-view directly, rather than sending the shell's "toggle
        // desktop icons" command (WM_COMMAND 0x7402).
        //
        // That command flips a state the shell keeps *globally*: the desktop's icons went away, and
        // so did the icons in every open Explorer window, which is not something a desktop
        // organiser should be doing to the user's file windows. Hiding the one list-view touches
        // nothing else - the other windows keep their icons and their own state.
        //
        // The desktop view itself stays alive, so the right-click "New" verb and the app's own
        // input forwarding keep working: the surface posts its mouse messages to the DefView when
        // the list-view is hidden, which is exactly the state this leaves behind.
        //
        // The user can still get their desktop icons back the same way as before - Explorer's
        // "Show desktop icons" re-shows the list-view, the app's visibility watch notices and
        // steps its surface aside.
        Win32Apis.ShowWindow(listView, hide ? 0 : 5); // SW_HIDE / SW_SHOW
    }
    public async Task SetDesktopIconsVisibleAsync(bool visible)
    {
        await Task.Run(() => SetDesktopIconsVisible(visible));
    }

    internal static IntPtr FindDesktopSHELLDLL_DefView()
    {
        IntPtr progman = Win32Apis.FindWindowEx(IntPtr.Zero, IntPtr.Zero, ShellWindowClasses.Progman, null);
        IntPtr defView = Win32Apis.FindWindowEx(progman, IntPtr.Zero, ShellWindowClasses.ShellDefView, null);
        if ((IntPtr)defView == IntPtr.Zero)
        {
            IntPtr worker = Win32Apis.FindWindowEx(IntPtr.Zero, IntPtr.Zero, ShellWindowClasses.WorkerW, null);
            while ((IntPtr)worker != IntPtr.Zero)
            {
                defView = Win32Apis.FindWindowEx(worker, IntPtr.Zero, ShellWindowClasses.ShellDefView, null);
                if ((IntPtr)defView != IntPtr.Zero)
                {
                    break;
                }

                worker = Win32Apis.FindWindowEx(IntPtr.Zero, worker, ShellWindowClasses.WorkerW, null);
            }
        }
        return (IntPtr)defView;
    }
    internal static IntPtr FindDesktopListView()
    {
        IntPtr progman = Win32Apis.FindWindowEx(IntPtr.Zero, IntPtr.Zero, ShellWindowClasses.Progman, null);
        IntPtr defView = Win32Apis.FindWindowEx(progman, IntPtr.Zero, ShellWindowClasses.ShellDefView, null);
        IntPtr listView = Win32Apis.FindWindowEx(defView, IntPtr.Zero, ShellWindowClasses.SysListView32, null);

        if ((IntPtr)listView == IntPtr.Zero)
        {
            IntPtr worker = Win32Apis.FindWindowEx(IntPtr.Zero, IntPtr.Zero, ShellWindowClasses.WorkerW, null);
            while ((IntPtr)worker != IntPtr.Zero)
            {
                defView = Win32Apis.FindWindowEx(worker, IntPtr.Zero, ShellWindowClasses.ShellDefView, null);
                listView = Win32Apis.FindWindowEx(defView, IntPtr.Zero, ShellWindowClasses.SysListView32, null);
                if ((IntPtr)listView != IntPtr.Zero)
                {
                    break;
                }

                worker = Win32Apis.FindWindowEx(IntPtr.Zero, worker, ShellWindowClasses.WorkerW, null);
            }
        }

        return (IntPtr)listView;
    }

    /// <summary>Returns the <c>WorkerW</c> window that hosts the Explorer <c>SHELLDLL_DefView</c>, or
    /// <see cref="IntPtr.Zero"/> if none is found.</summary>
    internal static IntPtr FindDesktopWorkerW()
    {
        IntPtr worker = Win32Apis.FindWindowEx(IntPtr.Zero, IntPtr.Zero, ShellWindowClasses.WorkerW, null);
        while ((IntPtr)worker != IntPtr.Zero)
        {
            IntPtr defView = Win32Apis.FindWindowEx(worker, IntPtr.Zero, ShellWindowClasses.ShellDefView, null);
            if ((IntPtr)defView != IntPtr.Zero)
            {
                return (IntPtr)worker;
            }

            worker = Win32Apis.FindWindowEx(IntPtr.Zero, worker, ShellWindowClasses.WorkerW, null);
        }

        return IntPtr.Zero;
    }
}
