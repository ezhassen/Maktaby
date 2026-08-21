using System.Collections.Generic;

namespace DesktopBoxesUI.Core.Models;

/// <summary>
/// The persisted "db file" (snapshot). Captures the desktop resolution the layout was saved at so
/// it can be rescaled proportionally when loaded on a different resolution (see the rescale logic in
/// <c>DesktopManager</c>).
/// </summary>
public sealed class DesktopSnapshot
{
    /// <summary>Primary work-area size (pixels) the containers' bounds were laid out against.</summary>
    public SizeD DesktopResolution { get; set; }

    /// <summary>All placed containers. Their bounds are stored in DIPs.</summary>
    public List<DesktopItemContainer> Containers { get; set; } = new();

    /// <summary>Box auto-routing rules (see <see cref="BoxRule"/>).</summary>
    public List<BoxRule> Rules { get; set; } = new();
}
