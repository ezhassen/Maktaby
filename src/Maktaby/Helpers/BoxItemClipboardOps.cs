using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using Maktaby.Core.Services;
using Maktaby.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Maktaby.Helpers;

/// <summary>
/// Cut / copy / paste for box items, on real files rather than on references.
/// </summary>
/// <remarks>
/// <para>
/// A box item is a reference to a desktop object, so a pure reference-level copy would give two
/// icons for one file and one of them would delete the file out from under the other. These
/// operations therefore follow Explorer on the filesystem:
/// </para>
/// <list type="bullet">
/// <item><b>Copy + paste</b> makes an actual duplicate on the Desktop, named "notes (1).txt", and
/// boxes the new file. Copying into the same box is allowed and is how you get a second icon.</item>
/// <item><b>Cut + paste</b> copies the file to the Desktop and then deletes the original, so the
/// item genuinely moves. Cutting into the box it already lives in is ignored, exactly as Explorer
/// ignores a move into the current folder.</item>
/// <item><b>Neither can duplicate a shell item.</b> A UWP app has no file to copy, so its
/// reference is what gets copied; a cut has to remove the source reference by hand, because there
/// is no file for the watcher to notice.</item>
/// </list>
/// <para>
/// The four routes a paste can take, which is why this reads in layers:
/// <list type="number">
/// <item><b>Cut, pasted into a different box</b> - the item REFERENCES move, exactly as dragging
/// one from box to box does. Both boxes are views over the same Desktop, so copying the file to
/// itself and deleting the original would be pointless churn on one disk.</item>
/// <item><b>Copy, pasted into any box</b> - a real duplicate is created on the Desktop and boxed,
/// because two icons for one file would make deleting either one destroy the other.</item>
/// <item><b>Cut, pasted into ANOTHER application</b> - nothing happens here at all. The files are
/// published as a move, so Explorer performs the real file move itself.</item>
/// <item><b>Files from another application</b> (clipboard has no Maktaby payload) - copied, or moved
/// when the other application asked for a move, and boxed on the Desktop.</item>
/// </list>
/// </para>
/// <para>
/// The original is deleted AFTER the copy has landed and after the watcher has resumed:
/// <c>FileRuleCoordinator</c> reacts to the deletion and drops the now-missing item from every
/// box, which is what clears the source icon. Pausing the watcher through the delete would leave
/// that stale icon behind, so the pause covers the copies only.
/// </para>
/// </remarks>
internal static class BoxItemClipboardOps
{
    /// <summary>
    /// Puts the items on the clipboard. A cut changes nothing else: the items stay in their box
    /// until the paste, so an abandoned cut loses nothing.
    /// </summary>
    public static bool SetClipboard(IEnumerable<BoxItem> items, bool cut) =>
        SetClipboard(items.Select(BoxItemTransfer.From), cut);

    /// <summary>
    /// Puts already-projected items on the clipboard. Folder-portal rows are not box items and
    /// have no <see cref="BoxItem"/> to project from, so they arrive as transfers.
    /// </summary>
    public static bool SetClipboard(IEnumerable<BoxItemTransfer> transfers, bool cut)
    {
        var clipboard = App.Services.GetService<IBoxItemClipboard>();
        if (clipboard is null) { return false; }

        var payload = new BoxItemClipboardPayload { Cut = cut };
        payload.Items.AddRange(transfers);

        return clipboard.Set(payload);
    }

    /// <summary>
    /// Pastes the clipboard's box items.
    /// </summary>
    /// <param name="target">
    /// Box to paste into, or <c>null</c> to create one - the desktop surface cannot receive key
    /// input (<c>WS_EX_NOACTIVATE</c>), so a paste issued with no box in play needs somewhere to
    /// land that the user can actually see.
    /// </param>
/// <param name="progress">
    /// Reported per copied FILE, on the same shape the file-drop path uses, so the caller can show
    /// its existing "Copying n of m" pill. Nothing is reported when the payload holds no file
    /// items, so pasting shell references never flashes an indicator.
    /// </param>
    /// <returns>The items added to the target box.</returns>
    public static async Task<IReadOnlyList<BoxItem>> PasteAsync(
        BoxViewModel? target,
        MainViewModel host,
        Action? requestSave,
        IProgress<DropHelper.DropProgress>? progress = null)
    {
        var clipboard = App.Services.GetService<IBoxItemClipboard>();
        var payload = clipboard?.Get();

        if (payload is not { HasItems: true })
        {
            // Not ours - the user copied files somewhere else. Paste those instead.
            var foreign = clipboard?.GetFileDrop();
            return foreign is { HasFiles: true }
                ? await PasteForeignFilesAsync(target, host, requestSave, foreign, progress)
                : Array.Empty<BoxItem>();
        }

        if (target is null)
        {
            var created = host.CreateBox();
            target = created.BoxContainerVm?.Tabs.FirstOrDefault();
            if (target is null) { return Array.Empty<BoxItem>(); }
        }

        var fileOps = App.Services.GetRequiredService<IFileOperationService>();
        var watcher = App.Services.GetRequiredService<IShellWatcherService>();

        var added = new List<BoxItem>();
        var staleShellItems = new List<(BoxViewModel Box, BoxItem Item)>();
        var originals = new List<string>();

        // Known up front so the caller's indicator can show "n of m" rather than a count that
        // keeps growing. Shell items are not files and are not counted.
        var fileTransfers = payload.Items.Count(i => !string.IsNullOrEmpty(i.Path));
        int copied = 0;

        watcher.Pause();
        bool resumed = false;
        try
        {
            foreach (var transfer in payload.Items)
            {
                var found = host.FindBoxWithItem(transfer);
                var source = found?.Box;
                var sourceItem = found?.Item;
                bool sameBox = source is not null && ReferenceEquals(source, target);

                if (payload.Cut && !sameBox && source is not null && sourceItem is not null)
                {
                    // A cut between boxes is a move of the reference, not a file operation: same
                    // disk, same desktop object, just a new home for the icon.
                    source.RemoveItem(sourceItem);
                    added.Add(transfer.ToItem());
                    continue;
                }

                if (string.IsNullOrEmpty(transfer.Path))
                {
                    // Shell item: no file to copy, so the reference is duplicated. A cut removes
                    // the source reference here because no deletion event will ever arrive.
                    if (sameBox) { continue; }
                    if (payload.Cut && source is not null && sourceItem is not null)
                    {
                        staleShellItems.Add((source, sourceItem));
                    }
                    added.Add(transfer.ToItem());
                    continue;
                }

                if (!File.Exists(transfer.Path) && !Directory.Exists(transfer.Path)) { continue; }
                if (ShellItemFilter.IsExcluded(transfer.Path)) { continue; }

                // A cut into the box it already lives in moves nothing.
                if (payload.Cut && sameBox) { continue; }

                // Reported BEFORE the copy as well as after: a single large file takes long enough
                // that waiting for the completion report means no indicator at all.
                progress?.Report(new DropHelper.DropProgress(
                    copied, fileTransfers, Path.GetFileName(transfer.Path)));

                var copy = await CopyToDesktopAsync(transfer.Path, fileOps);
                if (copy is null) { continue; }

                var item = BoxItemFactory.FromPath(copy);
                if (item is not null) { added.Add(item); }

                // Only now is the original expendable - the copy exists.
                if (payload.Cut) { originals.Add(transfer.Path); }

                progress?.Report(new DropHelper.DropProgress(
                    ++copied, fileTransfers, Path.GetFileName(transfer.Path)));
            }

            foreach (var item in added)
            {
                target.AddItem(item);
            }

            foreach (var (box, item) in staleShellItems)
            {
                box.RemoveItem(item);
            }

            if (added.Count > 0 || staleShellItems.Count > 0)
            {
                requestSave?.Invoke();
            }

            // Resume only now, once our items are in place. The copies land on the Desktop, and
            // the shell reports each one as a creation; FileRuleCoordinator would add it to the
            // DEFAULT box as well as ours, which is how one pasted file used to end up with two
            // icons in two boxes. Its AlreadyTracked guard vetoes the late notification because
            // the item is already registered, and anything that arrives while still suppressed is
            // dropped outright.
            watcher.Resume();
            resumed = true;

            // Deleting after the resume is deliberate: this is the one notification we DO want, so
            // the stale source item is dropped from every box. Non-permanent, so a move stays
            // recoverable from the Recycle Bin if the copy turns out to be wrong.
            if (originals.Count > 0)
            {
                await fileOps.DeleteAsync(originals, permanent: false);
            }
        }
        finally
        {
            if (!resumed) { watcher.Resume(); }
        }

        return added;
    }

    /// <summary>
    /// Pastes files that came from another application: each is copied onto the Desktop (so a box
    /// can own it) and boxed, and the original is deleted when that application asked for a move.
    /// </summary>
    private static async Task<IReadOnlyList<BoxItem>> PasteForeignFilesAsync(
        BoxViewModel? target,
        MainViewModel host,
        Action? requestSave,
        ForeignFileDrop drop,
        IProgress<DropHelper.DropProgress>? progress)
    {
        if (target is null)
        {
            var created = host.CreateBox();
            target = created.BoxContainerVm?.Tabs.FirstOrDefault();
            if (target is null) { return Array.Empty<BoxItem>(); }
        }

        var fileOps = App.Services.GetRequiredService<IFileOperationService>();
        var watcher = App.Services.GetRequiredService<IShellWatcherService>();

        var added = new List<BoxItem>();
        var originals = new List<string>();
        int processed = 0;

        watcher.Pause();
        bool resumed = false;
        try
        {
            foreach (var path in drop.Paths)
            {
                if (string.IsNullOrEmpty(path)) { continue; }
                if (!File.Exists(path) && !Directory.Exists(path)) { continue; }
                if (ShellItemFilter.IsExcluded(path)) { continue; }

                // Up front as well as after, so a slow first copy still shows the indicator.
                progress?.Report(new DropHelper.DropProgress(
                    processed, drop.Paths.Length, Path.GetFileName(path)));

                var copy = await CopyToDesktopAsync(path, fileOps);
                if (copy is null) { continue; }

                var item = BoxItemFactory.FromPath(copy);
                if (item is not null) { added.Add(item); }

                if (drop.IsMove) { originals.Add(path); }

                progress?.Report(new DropHelper.DropProgress(
                    ++processed, drop.Paths.Length, Path.GetFileName(path)));
            }

            if (added.Count == 0) { return Array.Empty<BoxItem>(); }

            foreach (var item in added)
            {
                target.AddItem(item);
            }

            requestSave?.Invoke();

            // Same ordering rule as the Maktaby-payload path: resume only after our items are
            // registered, or the coordinator files the same file into the default box as well.
            watcher.Resume();
            resumed = true;

            if (originals.Count > 0)
            {
                await fileOps.DeleteAsync(originals, permanent: false);
            }
        }
        finally
        {
            if (!resumed) { watcher.Resume(); }
        }

        return added;
    }

    /// <summary>
    /// Pastes into a folder portal's folder, with the shell's own copy/move rather than the
    /// Desktop materialisation a box needs. The portal's folder watcher picks up the result.
    /// </summary>
    /// <remarks>
    /// Sources come from whichever is available: a Maktaby payload (its file items) or another
    /// application's file drop. Shell items have no file and are skipped - a folder portal shows
    /// real files.
    /// </remarks>
    /// <returns>The destination paths that landed in the folder.</returns>
    public static async Task<IReadOnlyList<string>> PasteIntoFolderAsync(
        string folderPath,
        Action? requestSave,
        IProgress<DropHelper.DropProgress>? progress = null)
    {
        // Typed as the interface, not var: an inferred string[] would make `.Count` bind to the LINQ
        // extension method group instead of a property.
        IReadOnlyList<string> landed = Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(folderPath)) { return landed; }
        if (!Directory.Exists(folderPath)) { return landed; }

        var clipboard = App.Services.GetService<IBoxItemClipboard>();
        var payload = clipboard?.Get();

        var sources = new List<string>();
        bool move = false;

        if (payload is { HasItems: true })
        {
            sources.AddRange(payload.Items
                .Select(i => i.Path)
                .Where(p => !string.IsNullOrEmpty(p)));
            move = payload.Cut;
        }
        else if (clipboard?.GetFileDrop() is { HasFiles: true } drop)
        {
            sources.AddRange(drop.Paths);
            move = drop.IsMove;
        }

        sources = sources.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (sources.Count == 0) { return landed; }

        var fileOps = App.Services.GetRequiredService<IFileOperationService>();
        var watcher = App.Services.GetRequiredService<IShellWatcherService>();

        // Same NAME as the source, not a uniquified one: a paste into a folder that already holds
        // "notes.txt" must let the shell ask about replacing it, the way Explorer does. Pre-picking
        // "notes (1).txt" here is what silently created a second file instead.
        var destinations = sources
            .Select(p => Path.Combine(folderPath, Path.GetFileName(p)))
            .ToArray();

        // Recorded before the copy so a declined replacement is not mistaken for a fresh arrival:
        // after a "no" the destination still exists, but it is the file that was already there.
        var preExisting = destinations
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        watcher.Pause();
        bool copied;
        try
        {
            // A batched shell call reports nothing per file, so the indicator is stepped at both
            // ends rather than invented per item: up front so a slow cross-volume copy shows
            // something immediately, and once done so it clears with a real count.
            progress?.Report(new DropHelper.DropProgress(0, sources.Count, Path.GetFileName(sources[0])));

            // One shell call: it handles same-volume and cross-volume moves itself, which is the
            // whole reason for using it instead of File.Copy. confirmReplace lets the shell raise
            // its own replace prompt on a name collision.
            copied = await fileOps.CopyAsync(sources, destinations, confirmReplace: true);
        }
        finally
        {
            watcher.Resume();
        }

        if (!copied) { return landed; }

        // Only NEW arrivals count as landed. A destination that already existed is either the file
        // the user chose to replace (same path, not a new item) or one they declined.
        landed = destinations
            .Where(p => !preExisting.Contains(p) && (File.Exists(p) || Directory.Exists(p)))
            .ToArray();

        progress?.Report(new DropHelper.DropProgress(
            landed.Count, sources.Count, Path.GetFileName(sources[^1])));

        if (move)
        {
            // A move: the originals go away and the watcher drops whatever pointed at them.
            await fileOps.DeleteAsync(sources, permanent: false);
        }

        requestSave?.Invoke();
        return landed;
    }

    /// <summary>
    /// Copies <paramref name="source"/> to the Desktop under a free name, returning the new path
    /// or null when the copy failed.
    /// </summary>
    private static async Task<string?> CopyToDesktopAsync(string source, IFileOperationService fileOps)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop)) { return null; }

        var dest = DropHelper.MakeUnique(Path.Combine(desktop, Path.GetFileName(source)));

        if (await fileOps.CopyAsync(new[] { source }, new[] { dest })) { return dest; }

        // Fallback for the cases the shell copy declines (e.g. an in-flight handle on the source).
        try
        {
            if (File.Exists(source))
            {
                await Task.Run(() => File.Copy(source, dest, overwrite: false));
            }
            else
            {
                await Task.Run(() => CopyDirectory(source, dest));
            }
            return dest;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}