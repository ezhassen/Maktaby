using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;

namespace Maktaby.Helpers;

/// <summary>
/// Ensures only one instance of the app runs. The first instance holds a named <see cref="Mutex"/>
/// for its entire lifetime; any secondary instance activates the first and exits without
/// touching persistence or desktop state.
/// </summary>
public static class ApplicationSingleInstance
{
    private static Mutex? s_mutex;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>
    /// Best-effort: finds a visible top-level window belonging to the first instance.
    /// Falls back to <c>Process.MainWindowHandle</c> if enumeration yields nothing.
    /// </summary>
    private static IntPtr GetCurrentInstanceWindowHandle()
    {
        Process current = Process.GetCurrentProcess();
        string currentPath;
        try
        {
            currentPath = current.MainModule?.FileName ?? Assembly.GetExecutingAssembly().Location;
        }
        catch
        {
            currentPath = Assembly.GetExecutingAssembly().Location;
        }

        // 1) Find the other process with the same exe path.
        Process? other = null;
        try
        {
            foreach (var p in Process.GetProcessesByName(current.ProcessName))
            {
                if (p.Id == current.Id) continue;
                string otherPath;
                try { otherPath = p.MainModule?.FileName ?? string.Empty; }
                catch { continue; } // Access denied (elevated / other user)
                if (!string.Equals(otherPath, currentPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                other = p;
                break;
            }
        }
        catch { return IntPtr.Zero; }

        if (other is null) return IntPtr.Zero;

        // 2) Try Process.MainWindowHandle first — cheap and often sufficient for normal WPF apps.
        try
        {
            if (other.MainWindowHandle != IntPtr.Zero && IsWindowVisible(other.MainWindowHandle))
                return other.MainWindowHandle;
        }
        catch { }

        // 3) This app hosts boxes as WS_EX_TOOLWINDOW top-level windows and a hidden tray host;
        // MainWindowHandle is often 0. Enumerate all top-level windows and pick the first visible
        // one owned by the other process (BoxContainerWindow, Settings, About, or tray host).
        IntPtr found = IntPtr.Zero;
        try
        {
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid != (uint)other.Id) return true;
                // Prefer windows with a title or that are clearly our windows, but accept any visible one
                // to ensure the second instance at least activates something and doesn't silently exit.
                // Filter out the Windows shell's own windows by checking we don't pick Progman etc. — but
                // those belong to Explorer, not our pid, so no need.
                found = hWnd;
                return false; // stop
            }, IntPtr.Zero);
        }
        catch { }

        if (found != IntPtr.Zero) return found;

        // Last resort: whatever MainWindowHandle reports, even if not visible.
        try { return other.MainWindowHandle; } catch { return IntPtr.Zero; }
    }

    public static void SwitchToCurrentInstance()
    {
        IntPtr hWnd = GetCurrentInstanceWindowHandle();
        if (hWnd == IntPtr.Zero) return;

        try
        {
            if (IsIconic(hWnd))
                ShowWindow(hWnd, SW_RESTORE);
            SetForegroundWindow(hWnd);
        }
        catch { }
    }

    private static string GetMutexName()
    {
        // Use exe name + stable hash of exe path so side-by-side installs don't collide,
        // and Local\ so it doesn't require elevation (Global\ fails for non-admin on some OS configs).
        string exePath;
        try
        {
            exePath = Process.GetCurrentProcess().MainModule?.FileName
                      ?? Assembly.GetEntryAssembly()?.Location
                      ?? Assembly.GetExecutingAssembly().Location;
        }
        catch
        {
            exePath = Assembly.GetExecutingAssembly().Location;
        }

        string exeName = "Maktaby";
        try { exeName = Path.GetFileNameWithoutExtension(exePath); } catch { }

        // Short, stable, file-system safe — MUST be deterministic across processes.
        // string.GetHashCode() is randomized per-process in .NET, so two processes would
        // compute different hashes for the same exe path and never see each other's mutex.
        string hash;
        try
        {
            using var sha = SHA256.Create();
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(exePath.ToLowerInvariant()));
            hash = Convert.ToHexString(bytes)[..8];
        }
        catch
        {
            hash = "DEFAULT";
        }
        return $@"Local\{exeName}_SingleInstance_{hash}";
    }

    /// <summary>
    /// Tries to acquire the single-instance mutex. Returns true for the first instance (mutex held);
    /// false for any secondary instance (mutex already held). The mutex is held until <see cref="Release"/>
    /// or process exit — never released immediately.
    /// </summary>
    public static bool TryAcquire()
    {
        if (s_mutex is not null) return true; // already acquired in this process
        string name = GetMutexName();
        try
        {
            bool createdNew;
            // Do not use `using` — we must keep the handle open for the app lifetime.
            s_mutex = new Mutex(true, name, out createdNew);
            if (!createdNew)
            {
                // Another instance already holds it; dispose ours.
                try { s_mutex.Dispose(); } catch { }
                s_mutex = null;
                return false;
            }
            // Keep s_mutex alive; GC will not collect because of the static reference.
            // Do NOT ReleaseMutex here — that would free the mutex for a second instance.
            return true;
        }
        catch (AbandonedMutexException)
        {
            // Previous instance crashed without releasing; we now own it.
            return true;
        }
        catch
        {
            return true; // Fail-open: better to run than to block the user on mutex creation failure.
        }
    }

    public static void Release()
    {
        try { s_mutex?.ReleaseMutex(); } catch { }
        try { s_mutex?.Dispose(); } catch { }
        s_mutex = null;
    }

    /// <summary>
    /// Checks if the app is already running. If so, optionally activates the first instance
    /// and shuts down the current (secondary) process.
    /// Prefer <see cref="TryAcquire"/> in new code; this method is kept for compatibility
    /// with the tray test button.
    /// </summary>
    public static bool IsAlreadyRunning(bool shutdown = true, bool switchToCurrentInstance = true)
    {
        bool isFirst = TryAcquire();
        bool isAlreadyRunning = !isFirst;
        if (isAlreadyRunning)
        {
            if (switchToCurrentInstance) SwitchToCurrentInstance();
            if (shutdown && Application.Current is not null)
            {
                try { Application.Current.Shutdown(); } catch { }
            }
        }
        return isAlreadyRunning;
    }

    public static Task<bool> IsAlreadyRunningAsync(bool shutdownAndSwitchToCurrentInstance = true)
    {
        return Task.Run(() => IsAlreadyRunning(shutdownAndSwitchToCurrentInstance, shutdownAndSwitchToCurrentInstance));
    }
}
