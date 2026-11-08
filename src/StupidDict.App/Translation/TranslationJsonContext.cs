using System.Text.Json.Serialization;

namespace StupidDict.App.Translation;

/// <summary>
/// Source-generated JSON metadata for the outgoing chat/completions body.
/// Response parsing is JsonDocument.Parse (reflection-free) and needs no
/// context. RequestJsonOptions in TranslationService copies these options and
/// swaps the encoder — UnsafeRelaxedJsonEscaping keeps the &lt;text&gt; wrapper
/// literal because the body is application/json, never an HTML context.
/// </summary>
[JsonSourceGenerationOptions]
[JsonSerializable(typeof(TranslationRequestBody))]
internal sealed partial class TranslationJsonContext : JsonSerializerContext;
