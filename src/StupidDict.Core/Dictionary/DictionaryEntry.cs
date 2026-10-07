namespace StupidDict.Core.Dictionary;

/// <summary>A single headword with everything the UI needs to render it.</summary>
public sealed record DictionaryEntry(
    string Word,
    string Phonetic,
    string UsPhonetic,
    string Pos,
    string Chinese,
    string English,
    string Tags,
    int Freq,
    int Bnc);
