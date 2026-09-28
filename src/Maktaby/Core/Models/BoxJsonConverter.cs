using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maktaby.Core.Models;

/// <summary>Custom converter for <see cref="Box"/> that omits <see cref="Box.Items"/> when BoxType is FolderPortal.</summary>
public sealed class BoxJsonConverter : JsonConverter<Box>
{
    public override Box? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Clone options without this converter to avoid recursion for inner deserialization.
        var clone = new JsonSerializerOptions(options);
        // Remove this converter for inner call (avoid stack overflow). We create a fresh instance without converter.
        var self = clone.Converters.FirstOrDefault(c => c is BoxJsonConverter);
        if (self != null) clone.Converters.Remove(self);

        using var doc = JsonDocument.ParseValue(ref reader);
        var json = doc.RootElement.GetRawText();
        var box = JsonSerializer.Deserialize<BoxInternalDto>(json, clone);
        if (box is null) return null;
        return new Box
        {
            Id = box.Id,
            Name = box.Name ?? string.Empty,
            BoxType = box.BoxType,
            IsDefault = box.IsDefault,
            IconSize = box.IconSize,
            Items = box.Items ?? new System.Collections.ObjectModel.ObservableCollection<BoxItem>(),
            FolderPath = box.FolderPath,
            FolderPortalViewMode = box.FolderPortalViewMode,
            FolderSortBy = box.FolderSortBy,
            FolderSortAscending = box.FolderSortAscending
        };
    }

    public override void Write(Utf8JsonWriter writer, Box value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("Id", value.Id.ToString());
        writer.WriteString("Name", value.Name);
        writer.WriteNumber("BoxType", (int)value.BoxType);
        writer.WriteBoolean("IsDefault", value.IsDefault);
        if (value.IconSize.HasValue) writer.WriteNumber("IconSize", value.IconSize.Value);
        else writer.WriteNull("IconSize");

        // Only persist Items for DesktopItems. FolderPortal items are live FS and should not be saved.
        if (value.BoxType != BoxType.FolderPortal)
        {
            writer.WritePropertyName("Items");
            JsonSerializer.Serialize(writer, value.Items, options);
        }
        else
        {
            // Write empty array for compatibility / explicitness, or omit? We omit to keep file clean.
            // To keep older readers happy that expect Items, write empty.
            writer.WritePropertyName("Items");
            writer.WriteStartArray();
            writer.WriteEndArray();
        }

        if (value.FolderPath != null) writer.WriteString("FolderPath", value.FolderPath);
        else writer.WriteNull("FolderPath");
        writer.WriteNumber("FolderPortalViewMode", (int)value.FolderPortalViewMode);
        writer.WriteNumber("FolderSortBy", (int)value.FolderSortBy);
        writer.WriteBoolean("FolderSortAscending", value.FolderSortAscending);
        writer.WriteEndObject();
    }

    // Internal DTO for deserialization (without converter recursion)
    private sealed class BoxInternalDto
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public BoxType BoxType { get; set; }
        public bool IsDefault { get; set; }
        public int? IconSize { get; set; }
        public System.Collections.ObjectModel.ObservableCollection<BoxItem>? Items { get; set; }
        public string? FolderPath { get; set; }
        public FolderPortalViewMode FolderPortalViewMode { get; set; }
        public FolderSortMode FolderSortBy { get; set; }
        public bool FolderSortAscending { get; set; } = true;
    }
}
