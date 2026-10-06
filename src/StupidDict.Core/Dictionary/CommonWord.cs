namespace StupidDict.Core.Dictionary;

/// <summary>A headword with a corpus frequency rank (lower = more common); the fuzzy matcher's candidate set.</summary>
public sealed record CommonWord(string WordLower, int Freq);
