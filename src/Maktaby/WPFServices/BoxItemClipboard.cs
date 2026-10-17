using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace Maktaby.WPFServices;

/// <summary>
/// The WPF clipboard implementation for box items: the payload is serialized to JSON under one
/// private format name, and the file-backed items are additionally published as a normal
/// <see cref="DataFormats.FileDrop"/>.
/// </summary>
/// <remarks>
/// <para>
/// The retry is not paranoia. The clipboard is a single machine-wide object, and any process can
/// hold it open (another copy of Explorer, a remote-desktop client, a clipboard manager). WPF
/// surfaces that as <see cref="COMException"/> 0x800401D0, and without a retry a Ctrl+C pressed at
/// the wrong moment would silently do nothing - which for a user is indistinguishable from a
/// broken shortcut.
/// </para>
/// <para>
/// <c>copy: true</c> asks Windows to render the data on demand so the payload outlives the
/// process. The value is already a plain string, so there is no object to keep alive.
/// </para>
/// </remarks>
internal sealed class BoxItemClipboard : IBoxItemClipboard
{
    /// <summary>Private format; namespaced so it cannot collide with another app's.</summary>
    private const string Format = "Maktaby/BoxItems";

    /// <summary>
    /// The shell's convention for "what should happen to the sources". Explorer reads this to
    /// decide between leaving the originals (copy) and removing them (move), which is how a cut
    /// here becomes a real file move performed by the receiving application.
    /// </summary>
    private const string PreferredDropEffect = "Preferred DropEffect";

    private const int Attempts = 6;
    private const int RetryDelayMs = 12;

    public bool Set(BoxItemClipboardPayload payload)
    {
        if (payload is null || !payload.HasItems) { return false; }

        try
        {
            var json = JsonSerializer.Serialize(payload);
            var data = new DataObject(Format, json);

            // Also publish the real files, so Explorer (and anything else that reads a file drop)
            // can paste them. The drop effect is what turns a cut into a MOVE: Explorer performs
            // the file move and deletes the originals itself, which is the behaviour we want and
            // the reason the watcher stays quiet - nobody deletes anything here.
            var files = FileBackedPaths(payload);
            if (files.Length > 0)
            {
                data.SetData(DataFormats.FileDrop, files);

                // Stored as an int on purpose. "Preferred DropEffect" is a custom format that the
                // shell reads as a DROPEFFECT DWORD; handing WPF a boxed DragDropEffects would
                // serialize as the enum rather than the DWORD it expects to read.
                data.SetData(PreferredDropEffect, (int)(payload.Cut ? DragDropEffects.Move : DragDropEffects.Copy));
            }

            return Retry(() => Clipboard.SetDataObject(data, copy: true));
        }
        catch (Exception)
        {
            // Serialization of this payload cannot realistically fail (four string/enum fields
            // each), but a clipboard write must never take the app down.
            return false;
        }
    }

    public ForeignFileDrop? GetFileDrop()
    {
        try
        {
            IDataObject? data = null;
            if (!Retry(() => data = Clipboard.GetDataObject()) || data is null) { return null; }

            if (!data.GetDataPresent(DataFormats.FileDrop)) { return null; }

            object? raw = null;
            if (!Retry(() => raw = data.GetData(DataFormats.FileDrop))) { return null; }

            // Explorer hands files over as string[]; some sources hand over a DROPFILES HGLOBAL
            // instead, and WPF converts that to string[] while answering. An unrecognised shape is
            // treated as "nothing to paste" rather than being passed on as if it were paths.
            //
            // Note the limit of that guard: WPF's conversion dereferences the native handle, so a
            // malformed DROPFILES on the clipboard can fault inside the framework before this
            // check ever runs. Every WPF application that reads FileDrop has the same exposure -
            // it is the only API the clipboard offers - but do not mistake the check below for
            // protection against a hostile or corrupt producer.
            if (raw is not string[] paths || paths.Length == 0) { return null; }

            var effect = 0;
            try
            {
                if (data.GetData(PreferredDropEffect) is int i) { effect = i; }
            }
            catch
            {
                // No drop effect published: default to copy, which is what an absent effect means.
            }

            return new ForeignFileDrop
            {
                Paths = paths,
                IsMove = effect == (int)DragDropEffects.Move,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Paths of the payload's items that are real files or folders still present on disk.
    /// </summary>
    /// <remarks>
    /// Existence is checked on purpose: a file dropped between Ctrl+C and the paste must not reach
    /// another application as a path it cannot open. Shell-only items have no path and are simply
    /// absent - there is nothing to hand over for them.
    /// </remarks>
    private static string[] FileBackedPaths(BoxItemClipboardPayload payload) =>
        payload.Items
            .Select(i => i.Path)
            .Where(p => !string.IsNullOrEmpty(p))
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .ToArray();

    public BoxItemClipboardPayload? Get()
    {
        try
        {
            IDataObject? data = null;
            if (!Retry(() => data = Clipboard.GetDataObject()) || data is null) { return null; }

            if (!data.GetDataPresent(Format)) { return null; }

            object? json = null;
            if (!Retry(() => json = data.GetData(Format))) { return null; }

            if (json is not string text || text.Length == 0) { return null; }

            var payload = JsonSerializer.Deserialize<BoxItemClipboardPayload>(text);
            return payload is { HasItems: true } ? payload : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/>, retrying briefly while the clipboard is locked by another
    /// process. Only COMException is retried; anything else is a real fault.
    /// </summary>
    private static bool Retry(Action action)
    {
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (COMException)
            {
                // Clipboard busy. Back off and try again - the usual cause is milliseconds of
                // contention from another application.
                Thread.Sleep(RetryDelayMs);
            }
        }

        return false;
    }
}