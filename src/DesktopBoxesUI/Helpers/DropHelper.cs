using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using DesktopBoxesUI.Core.Services;
using DesktopBoxesUI.ViewModels;
using DesktopBoxesUI.Views;
using DesktopBoxesUI.Win32.NativeMethods;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace DesktopBoxesUI.Helpers;

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

        if (e.Data.GetDataPresent(DndFormats.BoxItems))
        {
            return Pick(DragDropEffects.Move, DragDropEffects.Copy);
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(ShellIdListFormat))
        {
            return Pick(DragDropEffects.Copy, DragDropEffects.Move, DragDropEffects.Link);
        }

        return DragDropEffects.None;
    }

    /// <summary>
    /// Applies the dropped data to <paramref name="target"/>. Sets <see cref="DragEventArgs.Handled"/>.
    /// <paramref name="insertIndex"/> (when &gt;= 0) reorders an internal item or inserts a moved item at
    /// a specific slot instead of appending. Returns the moved <see cref="BoxItem"/> models when the drop
    /// was an internal item move (so the caller can re-select them), otherwise <c>null</c>.
    /// </summary>
    public static List<BoxItem>? AddToBox(BoxViewModel target, MainViewModel? host, DragEventArgs e, int insertIndex = -1)
    {
        if (e.Data.GetDataPresent(DndFormats.BoxItems))
        {
            if (e.Data.GetData(DndFormats.BoxItems) is List<BoxItemViewModel> items && items.Count > 0)
            {
                var source = host?.FindBoxContaining(items[0]);
                if (source != null)
                {
                    // Move every dragged model in order. Remove all first (so a same-box reorder doesn't
                    // shift indices mid-loop), then (re)insert them at the target slot preserving order.
                    var movedModels = items.Select(i => i.Model).ToList();
                    foreach (var m in movedModels)
                    {
                        source.RemoveItem(m);
                    }

                    int at = insertIndex < 0 ? target.Items.Count : Math.Min(insertIndex, target.Items.Count);
                    foreach (var m in movedModels)
                    {
                        target.InsertItem(m, at);
                        at++;
                    }

                    e.Handled = true;
                    return movedModels;
                }
            }

            // Our format but nothing we could move: claim it so external handling doesn't misfire.
            e.Handled = true;
            return null;
        }

        var fileOps = App.Services.GetRequiredService<IFileOperationService>();
        var watcher = App.Services.GetRequiredService<IShellWatcherService>();

        var entries = GetShellItems(e).ToArray();

        watcher.Pause();
        try
        {
            int at = insertIndex < 0 ? -1 : Math.Min(insertIndex, target.Items.Count);
            foreach (var entry in entries)
            {
                BoxItem? boxItem = null;
                if (entry.FilePath != null)
                {
                    var resolved = ResolveDroppedFile(entry.FilePath, fileOps);
                    boxItem = BoxItemFactory.FromPath(resolved);
                }
                else if (entry.Pidl != null)
                {
                    var b64 = Convert.ToBase64String(entry.Pidl);
                    var resolved = ResolveDroppedPidl(b64, fileOps);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        boxItem = BoxItemFactory.FromPath(resolved);
                    }
                    else
                    {
                        boxItem = BoxItemFactory.FromShellPidl(b64, Win32Apis.GetPidlDisplayName(b64));
                    }
                }

                if (boxItem is not null)
                {
                    if (at >= 0)
                    {
                        target.InsertItem(boxItem, at);
                        at++;
                    }
                    else
                    {
                        target.AddItem(boxItem);
                    }
                }
            }
        }
        finally
        {
            watcher.Resume();
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(ShellIdListFormat))
        {
            e.Handled = true;
        }

        return null;
    }

    public static async Task<List<BoxItem>?> AddToBoxAsync(BoxViewModel target, MainViewModel? host, DragEventArgs e, int insertIndex = -1)
    {
        if (e.Data.GetDataPresent(DndFormats.BoxItems))
        {
            if (e.Data.GetData(DndFormats.BoxItems) is List<BoxItemViewModel> items && items.Count > 0)
            {
                var source = host?.FindBoxContaining(items[0]);
                if (source != null)
                {
                    var movedModels = items.Select(i => i.Model).ToList();
                    foreach (var m in movedModels) source.RemoveItem(m);
                    int at = insertIndex < 0 ? target.Items.Count : Math.Min(insertIndex, target.Items.Count);
                    foreach (var m in movedModels) { target.InsertItem(m, at); at++; }
                    e.Handled = true;
                    return movedModels;
                }
            }
            e.Handled = true;
            return null;
        }

        var fileOps = App.Services.GetRequiredService<IFileOperationService>();
        var watcher = App.Services.GetRequiredService<IShellWatcherService>();
        var entries = (await GetShellItemsAsync(e)).ToArray();
        watcher.Pause();
        try
        {
            // Batch resolve: collect file paths that need shell copy and PIDL entries
            var fileEntries = new List<(Win32Apis.ShellItemEntry entry, string b64)>();
            var resolvedItems = new List<(BoxItem? item, int origIdx)>();
            for (int i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry.FilePath != null)
                {
                    var resolved = await ResolveDroppedFileAsync(entry.FilePath, fileOps);
                    var bi = BoxItemFactory.FromPath(resolved);
                    resolvedItems.Add((bi, i));
                }
                else if (entry.Pidl != null)
                {
                    var b64 = Convert.ToBase64String(entry.Pidl);
                    var resolved = await ResolveDroppedPidlAsync(b64, fileOps);
                    BoxItem? bi = null;
                    if (!string.IsNullOrEmpty(resolved)) bi = BoxItemFactory.FromPath(resolved);
                    else bi = BoxItemFactory.FromShellPidl(b64, Win32Apis.GetPidlDisplayName(b64));
                    resolvedItems.Add((bi, i));
                }
            }

            int at = insertIndex < 0 ? -1 : Math.Min(insertIndex, target.Items.Count);
            foreach (var (bi, _) in resolvedItems.OrderBy(x => x.origIdx))
            {
                if (bi is null) continue;
                if (at >= 0) { target.InsertItem(bi, at); at++; }
                else target.AddItem(bi);
            }
        }
        finally { watcher.Resume(); }

        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(ShellIdListFormat))
            e.Handled = true;
        return null;
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
    private static string ResolveDroppedFile(string source, IFileOperationService fileOps)
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

            // Prefer the native shell copy (matches Explorer, handles links/dirs, supports Undo).
            if (fileOps.Copy(source, dest))
            {
                return dest;
            }

            // Fallback to a managed copy so the drop still materialises a desktop item.
            try
            {
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
        catch
        {
            return source;
        }
    }

    /// <summary>Resolves a dropped PIDL (e.g. a Start Menu shortcut) to a desktop file. When the PIDL points to a
    /// real file it is copied; when it is a virtual shell item (no filesystem path) a <c>.lnk</c> that stores the
    /// PIDL is materialised on the desktop (matching Explorer). Returns the desktop path, or <c>null</c> if nothing
    /// could be materialised.</summary>
    private static string? ResolveDroppedPidl(string pidlBase64, IFileOperationService fileOps)
    {
        var path = Win32Apis.GetPathFromPidl(pidlBase64);
        if (!string.IsNullOrEmpty(path))
        {
            return ResolveDroppedFile(path, fileOps);
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop))
        {
            return null;
        }

        var name = Win32Apis.GetPidlDisplayName(pidlBase64);
        if (string.IsNullOrEmpty(name))
        {
            name = "App";
        }

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        var dest = MakeUnique(Path.Combine(desktop, name + ".lnk"));
        return Win32Apis.CreateShortcutFromPidl(pidlBase64, dest) ? dest : null;
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

    private static async Task<string> ResolveDroppedFileAsync(string source, IFileOperationService fileOps)
    {
        try
        {
            if (string.IsNullOrEmpty(source) || (!File.Exists(source) && !Directory.Exists(source))) return source;
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop)) return source;
            var srcDir = Path.GetDirectoryName(source);
            if (srcDir != null && string.Equals(srcDir, desktop, StringComparison.OrdinalIgnoreCase)) return source;
            var dest = MakeUnique(Path.Combine(desktop, Path.GetFileName(source)));
            if (await fileOps.CopyAsync(new[] { source }, new[] { dest })) return dest;
            try
            {
                if (File.Exists(source)) await Task.Run(() => File.Copy(source, dest, false));
                else await CopyDirectoryAsync(source, dest);
                return dest;
            }
            catch { return source; }
        }
        catch { return source; }
    }

    private static async Task<string?> ResolveDroppedPidlAsync(string pidlBase64, IFileOperationService fileOps)
    {
        var path = Win32Apis.GetPathFromPidl(pidlBase64);
        if (!string.IsNullOrEmpty(path)) return await ResolveDroppedFileAsync(path, fileOps);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop)) return null;
        var name = Win32Apis.GetPidlDisplayName(pidlBase64);
        if (string.IsNullOrEmpty(name)) name = "App";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        var dest = MakeUnique(Path.Combine(desktop, name + ".lnk"));
        return Win32Apis.CreateShortcutFromPidl(pidlBase64, dest) ? dest : null;
    }

    private static Task<IEnumerable<Win32Apis.ShellItemEntry>> GetShellItemsAsync(DragEventArgs e)
    {
        return Task.FromResult(GetShellItems(e));
    }

    private static Task CopyDirectoryAsync(string source, string destination)
    {
        return Task.Run(() => CopyDirectory(source, destination));
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
