using DesktopBoxesUI.Core.Interfaces;
using DesktopBoxesUI.Core.Models;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopBoxesUI.Core.Services;

/// <summary>
/// JSON-file persistence for the desktop snapshot. Located under Core because it only uses the Base
/// Class Library (no Win32/Shell/WPF). The file lives in
/// %LocalAppData%/DesktopBoxes/boxes.snapshot.json.
/// </summary>
public sealed class JsonSnapshotPersistenceService : IPersistenceService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DesktopBoxes",
        "boxes.snapshot.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public async Task<DesktopSnapshot?> LoadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(FilePath);
            return await JsonSerializer.DeserializeAsync<DesktopSnapshot>(stream, Options, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
    public void SaveSnapshot(DesktopSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        using var stream = File.Create(FilePath);
        JsonSerializer.Serialize(stream, snapshot, Options);
    }
    public async Task SaveSnapshotAsync(DesktopSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await using var stream = File.Create(FilePath);
        await JsonSerializer.SerializeAsync(stream, snapshot, Options, cancellationToken).ConfigureAwait(false);
    }
}
