using System;
using System.Runtime.Versioning;
using Maktaby.Core.Interfaces;
using Maktaby.Win32.NativeMethods;
using Maktaby.Native;
using static Maktaby.Native.Win32Constants;

namespace Maktaby.Win32.Services;

/// <summary>
/// Resolves the live desktop icon size by measuring a real desktop icon rect (LVM_GETITEMRECT with
/// LVIR_ICON through the remote-buffer technique — image-list handles are process-local), falling
/// back to the system large-icon metric scaled by DPI.
///
/// Monitoring is LAYERED, because Windows has NO broadcast for desktop icon-size changes:
/// 1. WinEvent hook (EVENT_OBJECT_REORDER..LOCATIONCHANGE scoped to Explorer) — free fast path when
///    icons are visible and the list-view emits layout events.
/// 2. NotifyPossibleChange() — called by input paths we already see (the surface forwarding a
///    Ctrl+wheel to Explorer) for instant reaction.
/// 3. Slow safety-net timer (10s, Background priority) — REQUIRED: with icons hidden the list-view
///    itself is hidden (zero layout events), yet View-menu size changes still apply.
/// All three converge on one debounced <see cref="Refresh"/> that raises <see cref="Changed"/> only
/// on an actual value delta.
/// </summary>
[SupportedOSPlatform("windows10.0.14393")]
public sealed class DesktopIconSizeService : IDesktopIconSizeService
{
    private const uint EVENT_OBJECT_REORDER = 0x8004;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    private IntPtr _listView;
    private IntPtr _defView;
    private IntPtr _hook;
    private WinEventProc? _winEventProc;
    private bool _refreshQueued;
    private readonly System.Windows.Threading.DispatcherTimer _safetyNet;
    // Back-off: the safety-net exists for the hidden-icons case (zero layout events), but a
    // remote-proc read every 10s forever is wasteful when the value never moves. After a run of
    // stable ticks the interval stretches; any observed change snaps it back to fast.
    private static readonly TimeSpan FastInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SlowInterval = TimeSpan.FromSeconds(60);
    private const int StableTicksToSlow = 6;
    private int _stableTicks;

    public DesktopIconSizeService()
    {
        _current = Resolve();
        _safetyNet = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        _safetyNet.Tick += (_, _) => Refresh();
    }

    private int _current;

    public int Current => _current > 0 ? _current : 32;

    public event Action<int>? Changed;

    /// <summary>Installs (or re-installs after an Explorer restart) the location-change hook, resolves
    /// once and starts the safety-net timer. Safe to call multiple times.</summary>
    public void Start()
    {
        ReinstallHook();
        Refresh();
        if (!_safetyNet.IsEnabled)
        {
            _safetyNet.Start();
        }
    }

    /// <summary>Queue a debounced refresh — call from input paths that observe a possible icon-size
    /// gesture (e.g. the surface forwarding a Ctrl+wheel to Explorer). Cheap when idle.</summary>
    public void NotifyPossibleChange()
    {
        SetFastInterval();
        QueueRefresh();
    }

    /// <summary>Suspends monitoring: unhooks the WinEvent and stops the safety-net timer. The last
    /// resolved value stays available via <see cref="Current"/>.</summary>
    public void Stop()
    {
        _safetyNet.Stop();

        if (_hook != IntPtr.Zero)
        {
            Win32Apis.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }

    public void Refresh()
    {
        var size = Resolve();
        if (size > 0 && size != _current)
        {
            _current = size;
            Changed?.Invoke(size);
            SetFastInterval();
        }
        else if (_current <= 0)
        {
            _current = size;
        }
        else if (++_stableTicks >= StableTicksToSlow && _safetyNet.Interval != SlowInterval)
        {
            _safetyNet.Interval = SlowInterval;
        }
    }

    private void SetFastInterval()
    {
        _stableTicks = 0;
        if (_safetyNet.Interval != FastInterval)
        {
            _safetyNet.Interval = FastInterval;
        }
    }

    private void ReinstallHook()
    {
        if (_hook != IntPtr.Zero)
        {
            Win32Apis.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }

        _listView = ExplorerDesktopService.FindDesktopListView();
        _defView = ExplorerDesktopService.FindDesktopSHELLDLL_DefView();
        if (_listView == IntPtr.Zero)
        {
            return;
        }

        // SetWinEventHook filters by PROCESS/TREAD only — there is no hwnd parameter (the hwnd
        // arrives in the callback). Scoping to Explorer's PID keeps the callback volume sane; the
        // handler then matches the specific desktop windows.
        Win32Apis.GetWindowThreadProcessId(_listView, out uint explorerPid);

        _winEventProc ??= OnWinEvent;
        _hook = Win32Apis.SetWinEventHook(
            EVENT_OBJECT_REORDER,
            EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero,          // no DLL: WINEVENT_OUTOFCONTEXT callback lives in-process
            _winEventProc,
            explorerPid,          // only events originating from Explorer
            0,
            WINEVENT_OUTOFCONTEXT);
    }

    private void OnWinEvent(IntPtr hHook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Icon-size relayouts surface as layout events on either the list-view or its host DefView.
        if (_listView == IntPtr.Zero || (hwnd != _listView && hwnd != _defView))
        {
            return;
        }

        // Coalesce bursts (Ctrl+wheel / slider drags emit dozens): schedule ONE trailing refresh
        // after the burst settles. A new burst re-schedules because the flag clears when it runs.
        if (_refreshQueued)
        {
            return;
        }

        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
            {
                _refreshQueued = false;
                Refresh();
            }));

        // Trailing-edge refresh (queued above).
    }

    private static int Resolve()
    {
        // Preferred: measure a real desktop icon (item 0's LVIR_ICON rect height) through the same
        // remote-buffer technique as the hit-test — image-list handles are process-local, but item
        // rects give us the EXACT rendered icon pixels. Requires at least one desktop item.
        var listView = ExplorerDesktopService.FindDesktopListView();
        if (listView != IntPtr.Zero)
        {
            int count = (int)User32.SendMessage(listView, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero);
            if (count > 0 && TryGetItemIconHeight(listView, 0, out var iconPhys) && iconPhys > 0)
            {
                // The rect is in the list-view's PHYSICAL pixels; consumers (WPF) work in DIPs.
                // Normalize to the 96-DPI baseline so tiles match the desktop's VISUAL size.
                double scale = System.Math.Max(1d, Win32Apis.GetDpiForSystem() / 96.0);
                return System.Math.Max(16, (int)System.Math.Round(iconPhys / scale));
            }
        }

        try
        {
            // Fallback: SM_CXICON is the unscaled large-icon metric; scale to the system DPI.
            double scale = Win32Apis.GetDpiForSystem() / 96.0;
            return Math.Max(16, (int)Math.Round(User32.GetSystemMetrics(SM_CXICON) * scale));
        }
        catch
        {
            return 32;
        }
    }

    /// <summary>
    /// Reads item <paramref name="index"/>'s ICON sub-rect height from Explorer's list-view via a
    /// buffer allocated in ITS address space (LVM_GETITEMRECT carries a pointer; user32 does not
    /// marshal pointers across processes). Returns false when anything fails.
    /// </summary>
    private static bool TryGetItemIconHeight(IntPtr listView, int index, out int iconHeight)
    {
        iconHeight = 0;

        Win32Apis.GetWindowThreadProcessId(listView, out uint procId);
        if (procId == 0)
        {
            return false;
        }

        IntPtr hProc = Kernel32.OpenProcess(
            PROCESS_VM_OPERATION | PROCESS_VM_READ | PROCESS_VM_WRITE,
            false,
            procId);
        if (hProc == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            const uint MEM_COMMIT = 0x1000;
            const uint MEM_RESERVE = 0x2000;
            const uint MEM_RELEASE = 0x8000;
            const uint PAGE_READWRITE = 0x04;
            const uint rectSize = 16; // RECT: 4 ints

            IntPtr remote = Kernel32.VirtualAllocEx(hProc, IntPtr.Zero, rectSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remote == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                // left = LVIR_ICON; rest zeroed by VirtualAllocEx.
                byte[] input =
                {
                    (byte)LVIR_ICON, 0, 0, 0,
                    0, 0, 0, 0,
                    0, 0, 0, 0,
                    0, 0, 0, 0,
                };
                if (!Kernel32.WriteProcessMemory(hProc, remote, input, (uint)input.Length, out _))
                {
                    return false;
                }

                if (User32.SendMessage(listView, LVM_GETITEMRECT, (IntPtr)index, remote) == IntPtr.Zero)
                {
                    return false;
                }

                var outBytes = new byte[rectSize];
                if (!Kernel32.ReadProcessMemory(hProc, remote, outBytes, rectSize, out _) || outBytes.Length < 16)
                {
                    return false;
                }

                int top = BitConverter.ToInt32(outBytes, 4);
                int bottom = BitConverter.ToInt32(outBytes, 12);
                if (bottom <= top)
                {
                    return false;
                }

                iconHeight = bottom - top;
                return true;
            }
            finally
            {
                Kernel32.VirtualFreeEx(hProc, remote, 0, MEM_RELEASE);
            }
        }
        finally
        {
            Kernel32.CloseHandle(hProc);
        }
    }
}
