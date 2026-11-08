using System.Text.Json.Serialization;

namespace StupidDict.App.Settings;

/// <summary>
/// Source-generated JSON metadata for settings.json. NativeAOT has no
/// reflection-based serializer, so AppSettings (and its enum fields through
/// the property metadata) is rooted here. WriteIndented keeps the historical
/// hand-editable file shape; the forgiving enum converters are layered on top
/// in SettingsService as closed generics (AOT compiles those, it cannot run
/// the old Activator/MakeGenericType factory).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
