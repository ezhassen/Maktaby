using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Core.Services;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Win32.NativeMethods;

namespace DesktopBoxesUI.Views;

/// <summary>
/// Shared drag/drop logic for accepting items into a <see cref="BoxViewModel"/>, used by both a
/// Box (BoxControl) and the empty-area <see cref="DesktopSurface"/>. Handles internal Box-item
/// moves and external file drops (Start Menu / File Explorer / desktop).
/// </summary>
internal static class DropHelper
{
    private const string ShellIdListFormat = "Shell IDList Array";

    public static DragDropEffects GetEffect(DragEventArgs e)
    {
        var allowed = e.AllowedEffects;

        DragDropEffects Pick(params DragDropEffects[] preferred)
        {
            foreach (var effect in preferred)
            {
                if (allowed.HasFlag(effect))
                {
                    return effect;
                }
            }

            return DragDropEffects.None;
        }

        if (e.Data.GetDataPresent("DesktopBoxesItem"))
        {
            return Pick(DragDropEffects.Move, DragDropEffects.Copy);
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(ShellIdListFormat))
        {
            return Pick(DragDropEffects.Copy, DragDropEffects.Move, DragDropEffects.Link);
        }

        return DragDropEffects.None;
    }

    /// <summary>Applies the dropped data to <paramref name="target"/>. Sets <see cref="DragEventArgs.Handled"/>.</summary>
    public static void AddToBox(BoxViewModel target, MainViewModel? host, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("DesktopBoxesItem"))
        {
            if (e.Data.GetData("DesktopBoxesItem") is BoxItemViewModel item)
            {
                var source = host?.FindBoxContaining(item);
                if (source != null && source != target)
                {
                    source.RemoveItem(item.Model);
                    target.AddItem(item.Model);
                }

                e.Handled = true;
                return;
            }
        }

        var entries = GetShellItems(e).ToArray();

        int index = 0;
        foreach (var entry in entries)
        {
            BoxItem? boxItem = null;
            if (entry.FilePath != null)
            {
                var resolved = ResolveDroppedFile(entry.FilePath);
                boxItem = BoxItemFactory.FromPath(resolved);
            }
            else if (entry.Pidl != null)
            {
                var b64 = Convert.ToBase64String(entry.Pidl);
                boxItem = BoxItemFactory.FromShellPidl(b64, Win32Apis.GetPidlDisplayName(b64));
            }

            if (boxItem is not null)
            {
                target.AddItem(boxItem);
            }

            index++;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(ShellIdListFormat))
        {
            e.Handled = true;
        }
    }

    private static IEnumerable<Win32Apis.ShellItemEntry> GetShellItems(DragEventArgs e)
    {
        // FileDrop yields real filesystem paths (and an AppUserModelID for UWP apps), which is the
        // reliable source for ordinary drags. Resolve non-filesystem ids to PIDLs for icons/launch.
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            foreach (var p in paths)
            {
                if (File.Exists(p) || Directory.Exists(p))
                {
                    yield return new Win32Apis.ShellItemEntry { FilePath = p };
                }
                else
                {
                    byte[]? pidl = Win32Apis.GetPidlForAppId(p);
                    if (pidl != null)
                    {
                        yield return new Win32Apis.ShellItemEntry { Pidl = pidl };
                    }
                    else
                    {
                        yield return new Win32Apis.ShellItemEntry { FilePath = p };
                    }
                }
            }

            yield break;
        }

        // Fallback: raw Shell IDList (PIDL) when FileDrop is absent.
        byte[]? cida = GetShellIdListBytes(e);
        if (cida != null)
        {
            foreach (var entry in Win32Apis.GetShellIdListEntries(cida))
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// For a dropped filesystem path, copies the item into the user's Desktop folder (so the Box item
    /// is a real desktop file, like Fences). If the source is already on the Desktop, the path is used
    /// as-is. Virtual/shell items (returned as PIDLs) are handled separately.
    /// </summary>
    private static string ResolveDroppedFile(string source)
    {
        try
        {
            if (string.IsNullOrEmpty(source) || (!File.Exists(source) && !Directory.Exists(source)))
            {
                return source;
            }

            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop))
            {
                return source;
            }

            var srcDir = Path.GetDirectoryName(source);
            if (srcDir != null && string.Equals(srcDir, desktop, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }

            var dest = MakeUnique(Path.Combine(desktop, Path.GetFileName(source)));

            if (File.Exists(source))
            {
                File.Copy(source, dest, false);
            }
            else
            {
                CopyDirectory(source, dest);
            }

            return dest;
        }
        catch
        {
            return source;
        }
    }

    private static string MakeUnique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        int i = 1;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            i++;
        }
        while (File.Exists(candidate) || Directory.Exists(candidate));

        return candidate;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), false);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private static byte[]? GetShellIdListBytes(DragEventArgs e)
    {
        try
        {
            return e.Data.GetData(ShellIdListFormat) switch
            {
                byte[] raw => raw,
                MemoryStream ms => ms.ToArray(),
                Stream s => ReadAllBytes(s),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
