namespace Maktaby.Core.Interfaces;

/// <summary>
/// Extracts icons for shell items. The returned value is an opaque icon token; the WPF
/// layer converts it to an <c>ImageSource</c> via a converter. Kept abstract so Core stays
/// free of both WPF and Win32 image types.
/// </summary>
public interface IShellIconService
{
    /// <summary>Returns an opaque icon handle/token for the given path, or null if unavailable.</summary>
    System.Threading.Tasks.ValueTask<object?> GetIconAsync(string path, int size = 32, System.Threading.CancellationToken cancellationToken = default);
}
