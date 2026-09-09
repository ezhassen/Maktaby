namespace WindowsNative;

/// <summary>Shared native callback signatures. Declared once at namespace root so every consumer
/// references the same delegate types (a hook rooted as one type cannot be unhooked as another).</summary>
public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr lParam);
public delegate void WinEventProc(IntPtr hook, uint eventId, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime);
public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
