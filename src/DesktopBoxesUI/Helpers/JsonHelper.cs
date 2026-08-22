using Serilog.Events;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;

namespace DesktopBoxesUI.Helpers;


/// <summary>
/// Using System.Text.Json with custom Options and Converters
/// </summary>
public static class JsonHelper
{

    #region Props

    public static LogEventLevel? DefaultLogEventLevel
    {
        get
        {
            return field;
        }
        set
        {
            field = value;
            EnumValueAlias_LogEventLevel.DefaultValue = value;
        }
    }
    public static EnumValueAlias<LogEventLevel> EnumValueAlias_LogEventLevel
    {
        get
        {
            return field ??= new EnumValueAlias<LogEventLevel>(new(StringComparer.OrdinalIgnoreCase) {
                {"verbose" , LogEventLevel.Verbose},
                {"vrb" , LogEventLevel.Verbose},
                {"v" , LogEventLevel.Verbose},

                {"debug" , LogEventLevel.Debug},
                {"dbg" , LogEventLevel.Debug},
                {"d" , LogEventLevel.Debug},

                {"information" , LogEventLevel.Information},
                {"info" , LogEventLevel.Information},
                {"inf" , LogEventLevel.Information},
                {"i" , LogEventLevel.Information},

                {"warning" , LogEventLevel.Warning},
                {"warn" , LogEventLevel.Warning},
                {"wrn" , LogEventLevel.Warning},
                {"w" , LogEventLevel.Warning},

                {"error" , LogEventLevel.Error},
                {"err" , LogEventLevel.Error},
                {"e" , LogEventLevel.Error},

                {"fatal" , LogEventLevel.Fatal},
                {"ftl" , LogEventLevel.Fatal},
                {"f" , LogEventLevel.Fatal}
            }, defaultValue: DefaultLogEventLevel);
        }
    }

    public static JsonSerializerOptions DefaultJsonSerializerOptions
    {
        get
        {
            return field ??= new JsonSerializerOptions()
            {
                WriteIndented = true,
                //Encoder = System.Text.Unicode.Utf8;
                Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
                PropertyNameCaseInsensitive = true,
                //PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = {
                    // Add any custom converters here if needed
                    new LogEventLevelJsonEnumConverter(writeEnumToString: true),
                    new FlexibleJsonEnumConverterFactory(writeEnumToString: true),
                }

            };
        }
        //set { _JsonSerializerOptions = value; }
    }

    #endregion

    #region Register Converter

    /// <summary>
    /// Add FlexibleJsonEnumConverter with EnumValueAlias to <see cref="DefaultJsonSerializerOptions"/>. checks if type FlexibleJsonEnumConverter<T> is not registered and add it
    /// </summary>
    /// <typeparam name="T">Enum Type</typeparam>
    /// <param name="getEnumValueAlias">EnumValueAlias func to get when not in the converters list</param>
    public static void RegisterConverter_EnumValueAlias<T>(Func<EnumValueAlias<T>> getEnumValueAlias) where T : struct, Enum
    {
        Type enumAliasType = typeof(FlexibleJsonEnumConverter<T>);
        if (!DefaultJsonSerializerOptions.Converters.Any(c => c.GetType() == enumAliasType))
        {
            var newEConverter = new FlexibleJsonEnumConverter<T>(writeEnumToString: true, enumValueAlias: getEnumValueAlias());
            //Insert custom enum converter before the RuntimeEnumConverter
            DefaultJsonSerializerOptions.Converters.Insert(DefaultJsonSerializerOptions.Converters.Count - 1, newEConverter);
            //DefaultJsonSerializerOptions.Converters.Add(new FlexibleJsonEnumConverter<T>(writeEnumToString: true, enumValueAlias: getEnumValueAlias()));
        }
    }

    public static void RegisterConverter(JsonConverter jsonConverter)
    {
        Type jType = jsonConverter.GetType();
        if (!DefaultJsonSerializerOptions.Converters.Any(c => c.GetType() == jType))
        {
            //Insert custom converter before the RuntimeEnumConverter
            DefaultJsonSerializerOptions.Converters.Insert(DefaultJsonSerializerOptions.Converters.Count - 1, jsonConverter);
        }
    }

    #endregion

    #region  serialize/deserialize

    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo or JsonSerializerContext, or make sure all of the required types are preserved.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use System.Text.Json source generation for native AOT applications.")]
    public static T? DeserializeFromFile<T>(string filePath)
    {
        //using var fStream = File.OpenRead(filePath);
        using var fStream = new FileStream(
                                filePath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.ReadWrite);  // critical
        return JsonSerializer.Deserialize<T>(fStream, DefaultJsonSerializerOptions);
    }
    public static TValue? DeserializeFromFile<TValue>(string filePath, Func<JsonSerializerOptions, JsonTypeInfo<TValue>> jsonTypeInfoFactory)
    {
        //using var fStream = File.OpenRead(filePath);
        using var fStream = new FileStream(
                                filePath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.ReadWrite);  // critical
        return JsonSerializer.Deserialize<TValue>(fStream, jsonTypeInfoFactory(DefaultJsonSerializerOptions));
    }

    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo or JsonSerializerContext, or make sure all of the required types are preserved.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use System.Text.Json source generation for native AOT applications.")]
    public static async ValueTask<T?> DeserializeFromFileAsync<T>(string filePath)
    {
        //using var fStream = File.OpenRead(filePath);
        using var fStream = new FileStream(
                                filePath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.ReadWrite);  // critical
        return await JsonSerializer.DeserializeAsync<T>(fStream, DefaultJsonSerializerOptions);
    }

    public static async ValueTask<T?> DeserializeFromFileAsync<T>(string filePath, Func<JsonSerializerOptions, JsonTypeInfo<T>> jsonTypeInfoFactory)
    {
        //using var fStream = File.OpenRead(filePath);
        using var fStream = new FileStream(
                                filePath,
                                FileMode.Open,
                                FileAccess.Read,
                                FileShare.ReadWrite);  // critical
        return await JsonSerializer.DeserializeAsync<T>(fStream, jsonTypeInfoFactory(DefaultJsonSerializerOptions));
    }

    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo or JsonSerializerContext, or make sure all of the required types are preserved.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use System.Text.Json source generation for native AOT applications.")]
    public static TValue? Deserialize<TValue>(Stream stream)
    {
        return JsonSerializer.Deserialize<TValue>(stream, DefaultJsonSerializerOptions);
    }

    public static TValue? Deserialize<TValue>(Stream stream, Func<JsonSerializerOptions, JsonTypeInfo<TValue>> jsonTypeInfoFactory)
    {
        return JsonSerializer.Deserialize<TValue>(stream, jsonTypeInfoFactory(DefaultJsonSerializerOptions));
    }

    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo or JsonSerializerContext, or make sure all of the required types are preserved.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use System.Text.Json source generation for native AOT applications.")]
    public static ValueTask<TValue?> DeserializeAsync<TValue>(Stream stream)
    {
        return JsonSerializer.DeserializeAsync<TValue>(stream, DefaultJsonSerializerOptions);
    }

    public static ValueTask<TValue?> DeserializeAsync<TValue>(Stream stream, Func<JsonSerializerOptions, JsonTypeInfo<TValue>> jsonTypeInfoFactory)
    {
        return JsonSerializer.DeserializeAsync<TValue>(stream, jsonTypeInfoFactory(DefaultJsonSerializerOptions));
    }


    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo or JsonSerializerContext, or make sure all of the required types are preserved.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use System.Text.Json source generation for native AOT applications.")]
    public static TValue? Deserialize<TValue>(string json)
    {
        return JsonSerializer.Deserialize<TValue>(json, DefaultJsonSerializerOptions);
    }

    public static TValue? Deserialize<TValue>(string json, Func<JsonSerializerOptions, JsonTypeInfo<TValue>> jsonTypeInfoFactory)
    {
        return JsonSerializer.Deserialize<TValue>(json, jsonTypeInfoFactory(DefaultJsonSerializerOptions));
    }

    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo or JsonSerializerContext, or make sure all of the required types are preserved.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use System.Text.Json source generation for native AOT applications.")]
    public static void SerializeToFile<T>(string filePath, T obj)
    {
        //using var fStream = File.Create(filePath);
        using var fStream = new FileStream(
                                filePath,
                                FileMode.Create,
                                FileAccess.ReadWrite,
                                FileShare.ReadWrite);  // critical
        JsonSerializer.Serialize<T>(fStream, obj, DefaultJsonSerializerOptions);
    }

    public static void SerializeToFile<T>(string filePath, T obj, Func<JsonSerializerOptions, JsonTypeInfo<T>> jsonTypeInfoFactory)
    {
        //using var fStream = File.Create(filePath);
        using var fStream = new FileStream(
                                filePath,
                                FileMode.Create,
                                FileAccess.ReadWrite,
                                FileShare.ReadWrite);  // critical
        JsonSerializer.Serialize<T>(fStream, obj, jsonTypeInfoFactory(DefaultJsonSerializerOptions));
    }

    /// <summary>
    /// uses atomic replace (no lock conflict, watcher-safe)
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="filePath"></param>
    /// <param name="obj"></param>
    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo or JsonSerializerContext, or make sure all of the required types are preserved.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use System.Text.Json source generation for native AOT applications.")]
    public static void SafeWriteJson<T>(string filePath, T obj)
    {
        var tempFile = filePath + ".tmp";

        // Write to temp file with sharing allowed
        using (var fs = new FileStream(
            tempFile,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.ReadWrite))
        {
            JsonSerializer.Serialize(fs, obj, DefaultJsonSerializerOptions);
        }

        // Atomic replace (no lock conflict, watcher-safe)
        File.Copy(tempFile, filePath, true);
        File.Delete(tempFile);
    }

    public static void SafeWriteJson<T>(string filePath, T obj, Func<JsonSerializerOptions, JsonTypeInfo<T>> jsonTypeInfoFactory)
    {
        var tempFile = filePath + ".tmp";

        // Write to temp file with sharing allowed
        using (var fs = new FileStream(
            tempFile,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.ReadWrite))
        {
            JsonSerializer.Serialize(fs, obj, jsonTypeInfoFactory(DefaultJsonSerializerOptions));
        }

        // Atomic replace (no lock conflict, watcher-safe)
        File.Copy(tempFile, filePath, true);
        File.Delete(tempFile);
    }

    [RequiresUnreferencedCode("JSON serialization and deserialization might require types that cannot be statically analyzed. Use the overload that takes a JsonTypeInfo or JsonSerializerContext, or make sure all of the required types are preserved.")]
    [RequiresDynamicCode("JSON serialization and deserialization might require types that cannot be statically analyzed and might need runtime code generation. Use System.Text.Json source generation for native AOT applications.")]
    public static Task SerializeToFileAsync<T>(string filePath, T obj)
    {
        //using var fStream = File.Create(filePath);
        using var fStream = new FileStream(
                                filePath,
                                FileMode.Create,
                                FileAccess.ReadWrite,
                                FileShare.ReadWrite);  // critical
        return JsonSerializer.SerializeAsync<T>(fStream, obj, DefaultJsonSerializerOptions);
    }

    public static async Task SerializeToFileAsync<T>(string filePath, T obj, Func<JsonSerializerOptions, JsonTypeInfo<T>> jsonTypeInfoFactory)
    {
        //using var fStream = File.Create(filePath);
        using var fStream = new FileStream(
                                filePath,
                                FileMode.Create,
                                FileAccess.ReadWrite,
                                FileShare.ReadWrite);  // critical
        await JsonSerializer.SerializeAsync<T>(fStream, obj, jsonTypeInfoFactory(DefaultJsonSerializerOptions));
    }

    #endregion

    #region  Helpers

    public static bool TryGetLogEventLevelFromString(string str, out LogEventLevel logEventLevel)
    {
        return EnumValueAlias_LogEventLevel.TryRead(str, out logEventLevel);
    }

    public static LogEventLevel GetLogEventLevelFromString(string str)
    {
        return TryGetLogEventLevelFromString(str, out LogEventLevel logEventLevel)
            ? logEventLevel
            : throw new JsonException($"Unable to convert \"{str}\" to Enum \"{typeof(LogEventLevel)}\".");
    }

    static bool StringContainsAny(string str, params string[] strings)
    {
        foreach (var s in strings)
        {
            if (str.Contains(s, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static bool StringEqualsAny(string str, params string[] strings)
    {
        foreach (var s in strings)
        {
            if (str.Equals(s, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    internal static object? GetEnumObjectFromNumberFromReader(Utf8JsonReader reader, Type typeToConvert)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            Type underlying = Enum.GetUnderlyingType(typeToConvert);

            object value = Type.GetTypeCode(underlying) switch
            {
                TypeCode.Byte or TypeCode.SByte => reader.GetByte(),
                TypeCode.Int16 => reader.GetInt16(),
                TypeCode.UInt16 => reader.GetUInt16(),
                TypeCode.Int32 => reader.GetInt32(),
                TypeCode.UInt32 => reader.GetUInt32(),
                TypeCode.Int64 => reader.GetInt64(),
                _ => reader.GetUInt64()
            };
            return Enum.ToObject(typeToConvert, value);
        }
        return null;
    }

    internal static TEnum? GetEnumFromNumberFromReader<TEnum>(Utf8JsonReader reader) where TEnum : struct, Enum
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            Type underlying = Enum.GetUnderlyingType(typeof(TEnum));

            object value = Type.GetTypeCode(underlying) switch
            {
                TypeCode.Byte or TypeCode.SByte => reader.GetByte(),
                TypeCode.Int16 => reader.GetInt16(),
                TypeCode.UInt16 => reader.GetUInt16(),
                TypeCode.Int32 => reader.GetInt32(),
                TypeCode.UInt32 => reader.GetUInt32(),
                TypeCode.Int64 => reader.GetInt64(),
                _ => reader.GetUInt64()
            };
            return (TEnum)Enum.ToObject(typeof(TEnum), value);
        }
        return null;
    }
    #endregion
}

public class LogEventLevelJsonEnumConverter : FlexibleJsonEnumConverter<LogEventLevel>
{
    public LogEventLevelJsonEnumConverter() { }

    public LogEventLevelJsonEnumConverter(bool writeEnumToString)
    {
        this.WriteEnumToString = writeEnumToString;
    }

    public override LogEventLevel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return JsonHelper.GetLogEventLevelFromString(reader.GetString() ?? string.Empty);
        }
        var rEnumVal = JsonHelper.GetEnumFromNumberFromReader<LogEventLevel>(reader);
        if (rEnumVal.HasValue) return rEnumVal.Value;
        //if (reader.TokenType == JsonTokenType.Number)
        //{
        //    //var intVal = reader.GetInt32();
        //    //return (LogEventLevel)Enum.ToObject(typeof(LogEventLevel), intVal);
        //}
        throw new JsonException($"Unexpected token {reader.TokenType} when parsing enum. Expected a string or number.");
    }
}

public class FlexibleJsonEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public bool WriteEnumToString { get; set; } = true;
    public EnumValueAlias<T>? EnumValueAlias { get; set; }

    public FlexibleJsonEnumConverter() { }
    public FlexibleJsonEnumConverter(EnumValueAlias<T> enumValueAlias)
    {
        this.EnumValueAlias = enumValueAlias;
    }
    public FlexibleJsonEnumConverter(bool writeEnumToString)
    {
        this.WriteEnumToString = writeEnumToString;
    }
    public FlexibleJsonEnumConverter(bool writeEnumToString, EnumValueAlias<T> enumValueAlias)
    {
        this.WriteEnumToString = writeEnumToString;
        this.EnumValueAlias = enumValueAlias;
    }
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString() ?? string.Empty;
            if (this.EnumValueAlias is not null && this.EnumValueAlias.TryRead(str, out var enumValue)) return enumValue;

            if (Enum.TryParse<T>(str, ignoreCase: true, out var value)) return value;
            throw new JsonException($"Unable to convert \"{str}\" to Enum \"{typeof(T)}\".");
        }
        var rEnumVal = JsonHelper.GetEnumFromNumberFromReader<T>(reader);
        if (rEnumVal.HasValue) return rEnumVal.Value;
        //if (reader.TokenType == JsonTokenType.Number)
        //{
        //    var intVal = reader.GetInt32();
        //    return (T)Enum.ToObject(typeof(T), intVal);
        //}
        throw new JsonException($"Unexpected token {reader.TokenType} when parsing enum. Expected a string or number.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        if (this.WriteEnumToString)
        {
            // writer.WriteStringValue(value.ToString());
            writer.WriteStringValue(this.EnumValueAlias is not null ? this.EnumValueAlias.WriteToString(value) : value.ToString());
        }
        else
        {
            // writer.WriteNumberValue(Convert.ToInt32(value));
            writer.WriteNumberValue(this.EnumValueAlias is not null ? this.EnumValueAlias.WriteToNumber(value) : Convert.ToInt32(value));
        }
    }

}
public class RuntimeJsonEnumConverter : JsonConverter<object>
{
    public bool WriteEnumToString { get; set; } = true;
    public RuntimeJsonEnumConverter()
    {
    }
    public RuntimeJsonEnumConverter(bool writeEnumToString)
    {
        this.WriteEnumToString = writeEnumToString;
    }
    public override bool CanConvert(Type typeToConvert)
    {
        //typeToConvert.IsEnum;
        var enumType = Nullable.GetUnderlyingType(typeToConvert) ?? typeToConvert;
        return enumType.IsEnum;
    }

    public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            if (Nullable.GetUnderlyingType(typeToConvert) is not null) return null!;

            throw new JsonException($"Cannot convert null to {typeToConvert}.");
        }
        Type enumType = Nullable.GetUnderlyingType(typeToConvert) ?? typeToConvert;
        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (str is null) throw new JsonException($"Cannot convert null to enum {enumType}.");
            if (Enum.TryParse(enumType, str, ignoreCase: true, out var value)) return value;
            throw new JsonException($"Unable to convert \"{str}\" to enum \"{enumType}\".");
        }
        var rEnumVal = JsonHelper.GetEnumObjectFromNumberFromReader(reader, enumType);
        if (rEnumVal is not null) return rEnumVal;
        throw new JsonException($"Unexpected token {reader.TokenType} when parsing enum.");
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else if (this.WriteEnumToString)
        {
            writer.WriteStringValue(value.ToString());
        }
        else
        {
            writer.WriteNumberValue(Convert.ToInt32(value));
        }
    }
}

public class FlexibleJsonEnumConverterFactory : JsonConverterFactory
{
    public bool WriteEnumToString { get; set; } = true;

    public FlexibleJsonEnumConverterFactory() { }

    public FlexibleJsonEnumConverterFactory(bool writeEnumToString)
    {
        this.WriteEnumToString = writeEnumToString;
    }

    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert.IsEnum;
    }
    static RuntimeJsonEnumConverter SharedStringRuntimeEnumConverter => field ??= new RuntimeJsonEnumConverter(true);
    static RuntimeJsonEnumConverter SharedNumberRuntimeEnumConverter => field ??= new RuntimeJsonEnumConverter(false);
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        //var converterType = typeof(FlexibleJsonEnumConverter<>).MakeGenericType(typeToConvert);
        //return (JsonConverter)Activator.CreateInstance(converterType, args: this.WriteEnumToString)!;
        if (WriteEnumToString)
        {

            return SharedStringRuntimeEnumConverter;
        }
        else
        {
            return SharedNumberRuntimeEnumConverter;
        }
    }
}


public class EnumValueAlias<T> where T : struct, Enum
{
    Dictionary<string, T> EnumToAliasMapp { get; }
    public bool WriteEnumToString { get; set; } = true;

    /// <summary>
    /// The default value to use when the alias is not found. or cannot Read
    /// </summary>
    public T? DefaultValue { get; set; }

    /// <summary>
    /// Create an EnumValueAlias
    /// </summary>
    /// <param name="enumToAliasMapp">dic</param>
    /// <param name="defaultValue">The default value to use when the alias is not found. or cannot Read</param>
    public EnumValueAlias(Dictionary<string, T> enumToAliasMapp, T? defaultValue = null)
    {
        this.EnumToAliasMapp = enumToAliasMapp;
        this.DefaultValue = defaultValue;
    }

    public bool TryRead(string alias, out T enumValue)
    {
        if (Enum.TryParse<T>(alias, ignoreCase: true, out enumValue)) return true;
        if (this.DefaultValue.HasValue)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                enumValue = this.DefaultValue.Value;
                return true;
            }
            else if (this.EnumToAliasMapp.TryGetValue(alias, out enumValue))
            {
                return true;
            }

            enumValue = this.DefaultValue.Value;
            return true;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(alias)) return false;
            return this.EnumToAliasMapp.TryGetValue(alias, out enumValue);
        }
    }

    public T Read(string alias)
    {
        if (this.TryRead(alias, out T enumValue)) return enumValue;
        throw new InvalidOperationException($"Unable to convert \"{alias}\" to Enum \"{typeof(T)}\".");
    }

    public string WriteToString(T enumValue)
    {
        return enumValue.ToString();
    }

    public int WriteToNumber(T enumValue)
    {
        return Convert.ToInt32(enumValue);
    }
}