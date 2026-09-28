using Maktaby.Core.Interfaces;
using Maktaby.Core.Models;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Maktaby.Core.Services;

/// <summary>
/// JSON-file persistence for the desktop snapshot. Located under Core because it only uses the Base
/// Class Library (no Win32/Shell/WPF). The file lives in
/// %LocalAppData%/Maktaby/boxes.snapshot.json.
/// </summary>
public sealed class JsonSnapshotPersistenceService : IPersistenceService
{
    private static readonly string FilePath = Path.Combine(SettingsService.AppDataDir, "boxes.snapshot.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    // Single gate for all writers (sync + async) – prevents torn writes / File.Create truncation races
    // when SaveAsync fire-and-forgets overlap (DesktopManager.SaveAsyncFireAndForget).
    // Static so concurrent service instances (tests) still serialize.
    private static readonly SemaphoreSlim _writeGate = new(1, 1);

    public string SnapshotFilePath => FilePath;

    public void DeleteSnapshotFile()
    {
        _writeGate.Wait();
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        finally { _writeGate.Release(); }
    }

    public async Task<DesktopSnapshot?> LoadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<DesktopSnapshot>(stream, Options, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public void SaveSnapshot(DesktopSnapshot snapshot)
    {
        _writeGate.Wait();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmpPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
                JsonSerializer.Serialize(stream, snapshot, Options);
                stream.Flush(true);
            }
            catch
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                throw;
            }
            File.Move(tmpPath, FilePath, overwrite: true);
        }
        finally { _writeGate.Release(); }
    }

    public async Task SaveSnapshotAsync(DesktopSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmpPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await JsonSerializer.SerializeAsync(stream, snapshot, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                throw;
            }
            File.Move(tmpPath, FilePath, overwrite: true);
        }
        finally { _writeGate.Release(); }
    }

    public async Task<DesktopSnapshot?> LoadFromFileAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<DesktopSnapshot>(stream, Options, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
