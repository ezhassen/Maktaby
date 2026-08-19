using System;
using System.Collections.Generic;
using DesktopBoxesUI.Core.Models;

namespace DesktopBoxesUI.Core.Interfaces;

/// <summary>
/// Thin filesystem abstraction: existence checks, enumeration, and change watching.
/// Implemented using Win32/Shell notifications in the platform layer.
/// </summary>
public interface IFileSystemService
{
    bool Exists(string path);

    IEnumerable<string> Enumerate(string directory);

    /// <summary>
    /// Starts watching a path for changes. Returns a disposable that stops watching when disposed.
    /// Designed to be event-driven (no polling). The callback is invoked on an arbitrary thread.
    /// </summary>
    IDisposable Watch(string path, Action<FileSystemChange> onChange);
}
