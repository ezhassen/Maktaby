using Maktaby.Core.Models;

namespace Maktaby.Core.Interfaces;

/// <summary>
/// Carries a cut/copied set of <see cref="BoxItem"/>s across the system clipboard, so a box can
/// paste into another box - and so Explorer can paste the underlying files anywhere.
/// </summary>
/// <remarks>
/// Declared in Core with a plain model payload and no WPF types; the WPF implementation lives in
/// <c>WPFServices</c>. How the payload is expressed to other applications is the implementation's
/// business, but two things matter for the contract:
/// <list type="bullet">
/// <item>The <b>file</b> items are also published as a real file-drop list, so pasting into
/// Explorer, another file manager or a document's file dialog works - and a CUT carries the move
/// effect, which is what makes Explorer delete the originals itself rather than copy them.</item>
/// <item>Shell items (UWP apps, anything with only a PIDL) have no file to publish and are visible
/// to Maktaby alone. Pasting elsewhere silently skips them, which is the best that can be done.</item>
/// </list>
/// </remarks>
public interface IBoxItemClipboard
{
    /// <summary>
    /// Replaces the clipboard contents. Returns false when the clipboard could not be opened
    /// (another process is holding it), so a caller can report the failure instead of pretending
    /// the copy worked.
    /// </summary>
    /// <param name="payload">Items plus whether this is a cut. <c>Cut</c> decides the drop effect.</param>
    bool Set(BoxItemClipboardPayload payload);

    /// <summary>
    /// Reads the payload back, or null when the clipboard holds no box items. Never throws.
    /// </summary>
    BoxItemClipboardPayload? Get();

    /// <summary>
    /// Reads a plain file-drop list left by <b>another</b> application - Explorer, a file dialog,
    /// anything that puts files on the clipboard. Null when there are no files.
    /// </summary>
    /// <remarks>
    /// A paste falls back to this when <see cref="Get"/> has nothing, so copying files in Explorer
    /// and pressing Ctrl+V over a box does something sensible. Copy versus move is taken from the
    /// source application's drop effect, which is how a cut in Explorer arrives as a move rather
    /// than a duplicate.
    /// </remarks>
    ForeignFileDrop? GetFileDrop();
}