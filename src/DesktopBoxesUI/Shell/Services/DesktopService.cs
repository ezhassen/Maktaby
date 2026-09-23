using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Core.Services;
using WindowsNative;

namespace DesktopBoxesUI.Shell.Services;

/// <summary>
/// Enumerates the real Windows Desktop namespace through the Shell (not just filesystem entries),
/// so virtual items such as "This PC" and "Recycle Bin" are discovered alongside normal files and
/// folders. Runs synchronously on the caller's (STA) thread when iterated.
/// </summary>
public sealed class DesktopService : IDesktopService
{
    public async IAsyncEnumerable<BoxItem> GetDesktopItemsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Shell32.SHGetDesktopFolder(out IShellFolder desktop) != 0 || desktop is null)
        {
            yield break;
        }

        try
        {
            if (desktop.EnumObjects(
                    System.IntPtr.Zero,
                    SHCONTF.FOLDERS | SHCONTF.NONFOLDERS | SHCONTF.INCLUDEHIDDEN,
                    out IEnumIDList enumList) != 0 ||
                enumList is null)
            {
                yield break;
            }

            try
            {
                var pidls = new System.IntPtr[1];
                while (enumList.Next(1, pidls, out uint fetched) == 0 && fetched == 1)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pidl = pidls[0];
                    if (pidl == System.IntPtr.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        if (Shell32.SHCreateItemFromIDList(pidl, Shell32.IID_IShellItem, out IShellItem item) == 0 &&
                            item is not null)
                        {
                            try
                            {
                                var name = string.Empty;
                                if (item.GetDisplayName(SHGDNF.NORMAL, out System.IntPtr pName) == 0 && pName != System.IntPtr.Zero)
                                {
                                    try { name = Marshal.PtrToStringUni(pName) ?? string.Empty; }
                                    finally { Marshal.FreeCoTaskMem(pName); }
                                }

                                var parse = string.Empty;
                                if (item.GetDisplayName(SHGDNF.FORPARSING, out System.IntPtr pPath) == 0 && pPath != System.IntPtr.Zero)
                                {
                                    try { parse = Marshal.PtrToStringUni(pPath) ?? string.Empty; }
                                    finally { Marshal.FreeCoTaskMem(pPath); }
                                }

                                item.GetAttributes(SFGAO.FOLDER, out SFGAO attrs);
                                var type = (attrs & SFGAO.FOLDER) != 0 ? BoxItemType.Folder : BoxItemType.File;

                                if (ShellItemFilter.IsExcluded(parse))
                                {
                                    continue;
                                }

                                yield return new BoxItem
                                {
                                    Path = parse,
                                    DisplayName = name,
                                    ItemType = type,
                                };
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(item);
                            }
                        }
                    }
                    finally
                    {
                        Shell32.ILFree(pidl);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumList);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(desktop);
        }
    }
}
