using System.Text.Json;
using System.Text.Json.Serialization;

namespace StupidDict.App.Settings;

/// <summary>
/// JSON enum handling that degrades per field instead of per file. The stock
/// JsonStringEnumConverter throws JsonException on an unrecognized name — a
/// new enum value written by a newer build and read by an older one, or a
/// hand-edit typo — and SettingsService.Load's catch then discards the whole
/// settings.json: theme, language and window bounds all reset because one
/// field was stale. Here an unrecognized name (or a numeric string outside
/// the defined names) falls back to the enum's default and deserialization
/// continues. Defined names round-trip as strings exactly as before
/// (hand-editable, file format unchanged); bare numbers keep the stock
/// behavior — an undefined number still deserializes, landing on the app's
/// own fallback downstream.
/// </summary>
internal sealed class ForgivingEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(ForgivingEnumConverter<>).MakeGenericType(typeToConvert))!;
}

internal sealed class ForgivingEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            // Case-insensitive like the stock converter; IsDefined keeps
            // numeric strings that are not defined names on the default path.
            var name = reader.GetString();
            if (name is not null
                && Enum.TryParse<T>(name, ignoreCase: true, out var parsed)
                && Enum.IsDefined(parsed))
                return parsed;
            return default;
        }
        if (reader.TokenType == JsonTokenType.Number)
        {
            try
            {
                return (T)Enum.ToObject(typeof(T), reader.GetInt64());
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException)
            {
                return default;
            }
        }
        return default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        var name = Enum.GetName(value);
        if (name is not null)
            writer.WriteStringValue(name);
        else
            writer.WriteNumberValue(Convert.ToInt64(value));
    }
}
