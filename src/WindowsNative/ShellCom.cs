using System.Runtime.InteropServices;
using System.Text;

namespace WindowsNative;

/// <summary>Shared Shell COM interop (Desktop namespace, PIDLs, shell items/links, context menus).
/// Hand-rolled ComImport definitions only — the single canonical home for every Shell COM type
/// used by DesktopBoxesUI, DesktopLiveWallPaperEngine and WPFShared.</summary>

[ComImport]
[Guid("000214e4-0000-0000-c000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IContextMenu
{
    [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
    [PreserveSig] int GetCommandString(uint idCmd, uint uType, IntPtr pReserved, StringBuilder pszName, uint cchMax);
}

[ComImport]
[Guid("000214f4-0000-0000-c000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IContextMenu2
{
    [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
    [PreserveSig] int GetCommandString(uint idCmd, uint uType, IntPtr pReserved, StringBuilder pszName, uint cchMax);
    // Forwards owner-draw / menu-setup messages so the shell can render its items (e.g. the
    // "Run as administrator" shield) without corrupting state and crashing the host.
    [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct CMINVOKECOMMANDINFO
{
    public int cbSize;
    public int fMask;
    public IntPtr hwnd;
    public IntPtr lpVerb;
    public IntPtr lpParameters;
    public IntPtr lpDirectory;
    public int nShow;
    public int dwHotKey;
    public IntPtr hIcon;
}

[ComImport]
[Guid("000214E6-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellFolder
{
    void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName, out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
    [PreserveSig] int EnumObjects(IntPtr hwndOwner, SHCONTF grfFlags, out IEnumIDList ppenumIDList);
    void BindToObject(IntPtr pidl, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
    void BindToStorage(IntPtr pidl, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
    [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
    void CreateViewObject(IntPtr hwndOwner, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
    void GetAttributesOf(uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref SFGAO rgfInOut);
    void GetUIObjectOf(IntPtr hwndOwner, uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr rgfReserved, out IntPtr ppv);
    [PreserveSig] int GetDisplayNameOf(IntPtr pidl, SHGDNF uFlags, out STRRET pName);
    void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, uint uFlags, out IntPtr ppidlOut);
}

[ComImport]
[Guid("000214F2-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEnumIDList
{
    [PreserveSig] int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray)] IntPtr[] rgelt, out uint pceltFetched);
    [PreserveSig] int Skip(uint celt);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IEnumIDList ppenum);
}

[ComImport]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetParent(out IShellItem ppsi);
    [PreserveSig] int GetDisplayName(SHGDNF sigdnName, out IntPtr ppszName);
    [PreserveSig] int GetAttributes(SFGAO sfgaoMask, out SFGAO psfgaoAttribs);
    [PreserveSig] int Compare(IShellItem psi, int hint, out int piOrder);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct STRRET
{
    public uint uType;
    public IntPtr pOleStr;
}

[Flags]
public enum SHCONTF : uint
{
    FOLDERS = 0x20,
    NONFOLDERS = 0x40,
    INCLUDEHIDDEN = 0x80,
    INIT_ON_FIRST_NEXT = 0x100,
}

public enum SHGDNF : uint
{
    NORMAL = 0x0,
    INFOLDER = 0x1,
    FOREDITING = 0x1000,
    FORADDRESSBAR = 0x4000,
    FORPARSING = 0x8000,
}

[Flags]
public enum SFGAO : uint
{
    FOLDER = 0x20000000,
    FILESYSTEM = 0x40000000,
    STREAM = 0x00400000,
}

[ComImport]
[Guid("000214F9-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellLink
{
    [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
    [PreserveSig] int GetIDList(out IntPtr ppidl);
    [PreserveSig] int SetIDList(IntPtr pidl);
}

[ComImport]
[Guid("0000010B-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPersistFile
{
    [PreserveSig] int GetClassID(out Guid pClassID);
    [PreserveSig] int IsDirty();
    [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
    [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
    [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
    [PreserveSig] int GetCurFile(out IntPtr ppszFileName);
}

[ComImport]
[Guid("BCC6EEA8-5C45-44F0-8DF0-8B3B728B1728")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemImageFactory
{
    [PreserveSig] int GetImage(SIZE size, uint flags, out IntPtr phBitmap);
}

[ComImport, Guid("C2CF3110-460B-4d97-BF42-7ED4146F695C")]
public class DesktopWallpaperClass { }

[ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDesktopWallpaper
{
    void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
    [return: MarshalAs(UnmanagedType.LPWStr)] string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);
    [return: MarshalAs(UnmanagedType.LPWStr)] string GetMonitorDevicePathAt(uint monitorIndex);
    uint GetMonitorDevicePathCount();
    RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);
    void SetBackgroundColor(uint color);
    uint GetBackgroundColor();
    void SetPosition(DesktopWallpaperPosition position);
    DesktopWallpaperPosition GetPosition();
}

public enum DesktopWallpaperPosition { Center = 0, Tile = 1, Stretch = 2, Fit = 3, Fill = 4, Span = 5 }
