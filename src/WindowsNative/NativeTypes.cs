using System.Runtime.InteropServices;

namespace WindowsNative;

/// <summary>Shared blittable Win32 types. Single canonical home for every native struct used by
/// DesktopBoxesUI, DesktopLiveWallPaperEngine and WPFShared — hand-rolled DllImport only.</summary>

[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct SIZE
{
    public int Cx;
    public int Cy;
}

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;

    public RECT(int left, int top, int right, int bottom)
    {
        Left = left; Top = top; Right = right; Bottom = bottom;
    }

    public readonly bool IntersectsWith(in RECT other) =>
        Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;

    public readonly long IntersectionArea(in RECT other)
    {
        long w = Math.Min(Right, other.Right) - Math.Max(Left, other.Left);
        long h = Math.Min(Bottom, other.Bottom) - Math.Max(Top, other.Top);
        return w <= 0 || h <= 0 ? 0 : w * h;
    }

    public readonly long Area => (long)Width * Height;

    public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom}) {Width}x{Height}";
}

[StructLayout(LayoutKind.Sequential)]
public struct MSG
{
    public IntPtr Hwnd;
    public uint Message;
    public IntPtr WParam;
    public IntPtr LParam;
    public uint Time;
    public POINT Pt;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct WNDCLASSEX
{
    public uint Size;
    public uint Style;
    public WndProc WndProc;
    public int ClsExtra;
    public int WndExtra;
    public IntPtr Instance;
    public IntPtr Icon;
    public IntPtr Cursor;
    public IntPtr Background;
    [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
    [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
    public IntPtr IconSm;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct MONITORINFOEX
{
    public uint Size;
    public RECT Monitor;
    public RECT Work;
    public uint Flags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
}

[StructLayout(LayoutKind.Sequential)]
public struct MONITORINFO
{
    public uint Size;
    public RECT Monitor;
    public RECT Work;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
public struct BLENDFUNCTION
{
    public byte BlendOp;
    public byte BlendFlags;
    public byte SourceConstantAlpha;
    public byte AlphaFormat;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct NOTIFYICONDATA
{
    public uint Size;
    public IntPtr Hwnd;
    public uint Id;
    public uint Flags;
    public uint CallbackMessage;
    public IntPtr Icon;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
    public uint State;
    public uint StateMask;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
    public uint TimeoutOrVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
    public uint InfoFlags;
    public Guid GuidItem;
    public IntPtr BalloonIcon;
}

[StructLayout(LayoutKind.Sequential)]
public struct NOTIFYICONIDENTIFIER
{
    public uint Size;
    public IntPtr Hwnd;
    public uint Id;
    public Guid GuidItem;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct OPENFILENAME
{
    public uint StructSize;
    public IntPtr HwndOwner;
    public IntPtr Instance;
    [MarshalAs(UnmanagedType.LPWStr)] public string? Filter;
    [MarshalAs(UnmanagedType.LPWStr)] public string? CustomFilter;
    public uint MaxCustFilter;
    public uint FilterIndex;
    public IntPtr File;
    public uint MaxFile;
    [MarshalAs(UnmanagedType.LPWStr)] public string? FileTitle;
    public uint MaxFileTitle;
    [MarshalAs(UnmanagedType.LPWStr)] public string? InitialDir;
    [MarshalAs(UnmanagedType.LPWStr)] public string? Title;
    public uint Flags;
    public ushort FileOffset;
    public ushort FileExtension;
    [MarshalAs(UnmanagedType.LPWStr)] public string? DefExt;
    public IntPtr CustData;
    public IntPtr Hook;
    [MarshalAs(UnmanagedType.LPWStr)] public string? TemplateName;
    public IntPtr Reserved0;
    public uint Reserved1;
    public uint FlagsEx;
}

[StructLayout(LayoutKind.Sequential)]
public struct SYSTEM_POWER_STATUS
{
    public byte ACLineStatus;
    public byte BatteryFlag;
    public byte BatteryLifePercent;
    public byte SystemStatusFlag; // 1 = battery saver on
    public uint BatteryLifeTime;
    public uint BatteryFullLifeTime;
}

[StructLayout(LayoutKind.Sequential)]
public struct POWERBROADCAST_SETTING
{
    public Guid PowerSetting;
    public uint DataLength;
    public byte Data; // first byte; every setting used here is a single DWORD whose low byte carries the state
}

[StructLayout(LayoutKind.Sequential)]
public struct MEMORY_BASIC_INFORMATION
{
    public IntPtr BaseAddress;
    public IntPtr AllocationBase;
    public uint AllocationProtect;
    public IntPtr RegionSize;
    public uint State;
    public uint Protect;
    public uint Type;
}

/// <summary>Per-process I/O accounting from GetProcessIoCounters (all cumulative since
/// process start; transfer counts are bytes covering disk, network and device I/O).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct IO_COUNTERS
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

/// <summary>SHFILEINFO for SHGetFileInfo.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct SHFILEINFOW
{
    public IntPtr hIcon;
    public int iIcon;
    public uint dwAttributes;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string szDisplayName;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
    public string szTypeName;
}

/// <summary>Flags for SHGetFileInfo.</summary>
[Flags]
public enum SHGFI : uint
{
    Icon = 0x00000100,
    SmallIcon = 0x00000001,
    LargeIcon = 0x00000000,
    UseFileAttributes = 0x00000010,
    AddOverlays = 0x00000020,
    OpenIcon = 0x00000002,
    Pidl = 0x00000008,
}

/// <summary>Flags for ShellExecuteEx.</summary>
[Flags]
public enum SEE_MASK : uint
{
    IDLIST = 0x00000004,
    NO_UI = 0x00000400,
    INVOKEIDLIST = 0x0000000C,
}

/// <summary>Used by ShellExecuteEx to launch a shell item by PIDL.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct SHELLEXECUTEINFO
{
    public int cbSize;
    public SEE_MASK fMask;
    public IntPtr hwnd;
    public string? lpVerb;
    public string? lpFile;
    public string? lpParameters;
    public string? lpDirectory;
    public int nShow;
    public IntPtr hInstApp;
    public IntPtr lpIDList;
    public string? lpClass;
    public IntPtr hkeyClass;
    public uint dwHotKey;
    public IntPtr hIconOrMonitor;
    public IntPtr hProcess;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct SHFILEOPSTRUCT
{
    public IntPtr hwnd;
    public uint wFunc;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string? pFrom;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string? pTo;
    public uint fFlags;
    public int fAnyOperationsAborted;
    public IntPtr hNameMappings;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string? lpszProgressTitle;
}

/// <summary>Hit-test info for LVM_HITTEST (desktop icon vs empty area).</summary>
[StructLayout(LayoutKind.Sequential)]
public struct LVHITTESTINFO
{
    public POINT pt;
    public uint flags;
    public int iItem;
    public int iSubItem;
    public int iGroup;
}

[StructLayout(LayoutKind.Sequential)]
public struct TRACKMOUSEEVENT
{
    public uint cbSize;
    public uint dwFlags;
    public IntPtr hwndTrack;
    public uint dwHoverTime;
}

[StructLayout(LayoutKind.Sequential)]
public struct PAINTSTRUCT
{
    public IntPtr Hdc;
    public bool Erase;
    public RECT Paint;
    public bool Restore;
    public bool IncUpdate;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved;
}

[StructLayout(LayoutKind.Sequential)]
public struct SHChangeNotifyEntry
{
    public IntPtr pidl;
    public int fRecursive;
}

[Flags]
public enum SHCNRF : int
{
    InterruptLevel = 0x0001,
    ShellLevel = 0x0002,
    RecursiveInterrupt = 0x1000,
    NewDelivery = 0x8000,
}

[Flags]
public enum SHCNE : int
{
    RENAMEITEM = 0x0001,
    CREATE = 0x0002,
    DELETE = 0x0004,
    RENAMEFOLDER = 0x0008,
    UPDATEITEM = 0x00002000,
}

/// <summary>Toolhelp32 process snapshot entry (Process32FirstW/NextW). Only the PID,
/// parent PID and exe name are consumed; the rest keeps the layout blittable.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct PROCESSENTRY32W
{
    public uint Size;
    public uint Usage;
    public uint ProcessID;
    public IntPtr DefaultHeapID;
    public uint ModuleID;
    public uint Threads;
    public uint ParentProcessID;
    public int PriClassBase;
    public uint Flags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
}
