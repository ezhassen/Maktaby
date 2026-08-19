using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Core.Services;

/// <summary>
/// JSON-file persistence for boxes (the snapshot / "db file"). Located under Core because it
/// only uses the Base Class Library (no Win32/Shell/WPF). The file lives in
/// %LocalAppData%/DesktopBoxes/boxes.snapshot.json.
/// </summary>
public sealed class JsonBoxPersistenceService : IPersistenceService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopBoxes",
        "boxes.snapshot.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public async Task<IReadOnlyList<Box>?> LoadBoxesAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(FilePath);
            var boxes = await JsonSerializer.DeserializeAsync<List<Box>>(stream, Options, cancellationToken).ConfigureAwait(false);
            return boxes;
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveBoxesAsync(IEnumerable<Box> boxes, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await using var stream = File.Create(FilePath);
        await JsonSerializer.SerializeAsync(stream, new List<Box>(boxes), Options, cancellationToken).ConfigureAwait(false);
    }
}
