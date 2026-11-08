using System.Text.Json.Serialization;

namespace StupidDict.App.Translation;

/// <summary>
/// chat/completions request body. The wire contract is lowercase property
/// names, pinned via [JsonPropertyName] (declared-name order keeps system
/// before user); with a source-gen context these DTOs serialize without the
/// reflection serializer that NativeAOT cannot run. The previous anonymous
/// type spelled its property names lowercase to get the same JSON.
/// </summary>
internal sealed record TranslationRequestBody(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] TranslationMessage[] Messages,
    [property: JsonPropertyName("temperature")] double Temperature,
    [property: JsonPropertyName("stream")] bool Stream);

internal sealed record TranslationMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);
