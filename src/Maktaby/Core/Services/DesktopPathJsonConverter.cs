using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maktaby.Core.Services;

/// <summary>
/// (De)serialises a <see cref="BoxItem.Path"/> as a desktop-relative, portable token when the item
/// lives under the current or public desktop, and resolves it back to a full path on read.
/// </summary>
public sealed class DesktopPathJsonConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value is null ? null : DesktopPathHelper.ToFullPath(value);
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(DesktopPathHelper.ToPortable(value));
    }
}
