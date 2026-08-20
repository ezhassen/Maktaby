using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopBoxesUI.Shell.Interop;

/// <summary>
/// Isolated Shell COM interop used to enumerate the real Desktop namespace (including virtual items
/// such as "This PC" and "Recycle Bin") and to resolve item icons by PIDL. Per the architecture
/// rules, Shell COM native interop lives in <c>Shell/Interop</c> only.
/// </summary>
internal static class ShellNative
{
    public static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    [DllImport("shell32.dll")]
    public static extern int SHGetDesktopFolder(out IShellFolder ppshf);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateItemFromIDList(IntPtr pidl, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IShellItem ppv);

    [DllImport("shell32.dll")]
    public static extern int SHGetIDListFromObject(IntPtr punk, out IntPtr ppidl);

    [DllImport("shell32.dll")]
    public static extern void ILFree(IntPtr pidl);

    public static readonly Guid IID_IContextMenu = new("000214e4-0000-0000-c000-000000000046");
}

[ComImport]
[Guid("000214e4-0000-0000-c000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu
{
    [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
    [PreserveSig] int GetCommandString(uint idCmd, uint uType, IntPtr pReserved, StringBuilder pszName, uint cchMax);
}

[ComImport]
[Guid("000214f4-0000-0000-c000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IContextMenu2
{
    [PreserveSig] int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
    [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFO pici);
    [PreserveSig] int GetCommandString(uint idCmd, uint uType, IntPtr pReserved, StringBuilder pszName, uint cchMax);
    // Forwards owner-draw / menu-setup messages so the shell can render its items (e.g. the
    // "Run as administrator" shield) without corrupting state and crashing the host.
    [PreserveSig] int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct CMINVOKECOMMANDINFO
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
internal interface IShellFolder
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
internal interface IEnumIDList
{
    [PreserveSig] int Next(uint celt, [Out, MarshalAs(UnmanagedType.LPArray)] IntPtr[] rgelt, out uint pceltFetched);
    [PreserveSig] int Skip(uint celt);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IEnumIDList ppenum);
}

[ComImport]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetParent(out IShellItem ppsi);
    [PreserveSig] int GetDisplayName(SHGDNF sigdnName, out IntPtr ppszName);
    [PreserveSig] int GetAttributes(SFGAO sfgaoMask, out SFGAO psfgaoAttribs);
    [PreserveSig] int Compare(IShellItem psi, int hint, out int piOrder);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct STRRET
{
    public uint uType;
    public IntPtr pOleStr;
}

[Flags]
internal enum SHCONTF : uint
{
    FOLDERS = 0x20,
    NONFOLDERS = 0x40,
    INCLUDEHIDDEN = 0x80,
    INIT_ON_FIRST_NEXT = 0x100,
}

internal enum SHGDNF : uint
{
    NORMAL = 0x0,
    INFOLDER = 0x1,
    FOREDITING = 0x1000,
    FORADDRESSBAR = 0x4000,
    FORPARSING = 0x8000,
}

[Flags]
internal enum SFGAO : uint
{
    FOLDER = 0x20000000,
    FILESYSTEM = 0x40000000,
    STREAM = 0x00400000,
}
