using System.Collections.Generic;
using DesktopBoxesUI.Core.Interfaces;

namespace DesktopBoxesUI.Core.Services;

/// <summary>
/// Volatile, in-memory settings implementation. Persistence to disk/json will replace
/// this later; the rest of the app only depends on <see cref="ISettingsService"/>.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly Dictionary<string, object?> _values = new();

    public void Load()
    {
        // No-op for the in-memory stub.
    }

    public void Save()
    {
        // No-op for the in-memory stub.
    }

    public T? GetValue<T>(string key) where T : struct
    {
        if (_values.TryGetValue(key, out var value) && value is T typed)
        {
            return typed;
        }

        return default;
    }

    public void SetValue<T>(string key, T value)
    {
        _values[key] = value;
    }
}
