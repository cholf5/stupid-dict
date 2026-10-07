using System.Globalization;
using System.Text.RegularExpressions;
using StupidDict.Core.Dictionary;

/// <summary>
/// Builds the thesaurus (近义词/反义词) from the WordNet database files
/// (dict/index.* and dict/data.*). For every headword present in both WordNet
/// and the word table:
///
///   近义词 — co-lemmas of each synset, plus the lemmas of the hypernym for
///   noun/verb senses and of the "similar to" satellites for adjective senses;
///
///   反义词 — all lemmas of each antonym synset (adjective heads also pull in
///   their satellites), which is how the antonym side carries variants like
///   "evil, evilness".
///
/// 联想词 is not stored: at query time the store ranks this synonym/antonym
/// pool by corpus frequency and glosses it from the word table.
/// </summary>
internal static partial class WordNetThesaurus
{
    private const int MaxWordsPerLine = 12;

    private static readonly string[] PosFiles = ["noun", "verb", "adj", "adv"];
    private static readonly char[] InsertionOrder = ['n', 'v', 'a', 'r'];

    private static readonly Regex MarkerRegex = new(@"\([^)]*\)$", RegexOptions.Compiled);

    private sealed class Synset(char pos, List<string> lemmas, List<Pointer> pointers)
    {
        public readonly char Pos = pos;
        public readonly List<string> Lemmas = lemmas;
        public readonly List<Pointer> Pointers = pointers;
    }

    private readonly record struct Pointer(char Symbol, int Offset, char Pos);

    /// <summary>Returns (headwords written, syn_group rows written).</summary>
    public static (long Words, long Lines) Build(DictionaryDatabase db, string dictDir)
    {
        var index = ReadIndex(dictDir);
        var data = ReadSynsets(dictDir);

        var wordIds = new Dictionary<string, long>();
        long? WordId(string normalized)
        {
            if (wordIds.TryGetValue(normalized, out var cached)) return cached == 0 ? null : cached;
            var id = db.FindWordId(normalized);
            wordIds[normalized] = id ?? 0;
            return id;
        }

        long words = 0, lines = 0;
        foreach (var (lemma, entries) in index)
        {
            if (WordId(lemma) is not { } wordId) continue;

            var synonyms = Collect(data, lemma, entries);
            foreach (var (kind, pools) in new[] { ("syn", synonyms), ("ant", CollectAntonyms(data, lemma, entries)) })
            {
                foreach (var posChar in InsertionOrder)
                {
                    if (!pools.TryGetValue(posChar, out var pool) || pool.Count == 0) continue;

                    // Only headwords that actually exist in the dictionary: every
                    // thesaurus word becomes a clickable link.
                    List<string> kept = [];
                    HashSet<string> seen = new(StringComparer.Ordinal);
                    foreach (var word in pool)
                    {
                        if (kept.Count >= MaxWordsPerLine) break;
                        if (WordId(word) is null || !seen.Add(word)) continue;
                        kept.Add(word);
                    }
                    if (kept.Count == 0) continue;

                    db.InsertSynGroup(wordId, kind, DisplayPos(posChar), string.Join(", ", kept));
                    lines++;
                }
            }
            words++;
        }
        return (words, lines);
    }

    /// <summary>Synonyms per source-sense POS: synset co-lemmas + hypernyms (n/v) + similar satellites (adj).</summary>
    private static Dictionary<char, List<string>> Collect(Dictionary<int, Synset> data, string lemma,
        List<(char Pos, int[] Offsets)> entries)
    {
        Dictionary<char, List<string>> pools = new();
        var self = lemma.ToLowerInvariant();

        void Add(char pos, IEnumerable<string> words)
        {
            if (!pools.TryGetValue(pos, out var pool))
                pools[pos] = pool = [];
            foreach (var word in words)
                if (!word.Equals(self, StringComparison.OrdinalIgnoreCase))
                    pool.Add(word);
        }

        foreach (var (_, offsets) in entries)
            foreach (var offset in offsets)
            {
                if (!data.TryGetValue(offset, out var synset)) continue;
                Add(synset.Pos, synset.Lemmas);

                foreach (var pointer in synset.Pointers)
                {
                    if (pointer.Symbol == '@' && synset.Pos is 'n' or 'v'
                        && data.TryGetValue(pointer.Offset, out var hypernym))
                        Add(synset.Pos, hypernym.Lemmas);
                    else if (pointer.Symbol == '&' && synset.Pos is 'a' or 's'
                        && data.TryGetValue(pointer.Offset, out var similar))
                        Add(synset.Pos, similar.Lemmas);
                }
            }
        return pools;
    }

    /// <summary>Antonyms per source-sense POS: every lemma of each antonym synset, plus its satellites.</summary>
    private static Dictionary<char, List<string>> CollectAntonyms(Dictionary<int, Synset> data, string lemma,
        List<(char Pos, int[] Offsets)> entries)
    {
        Dictionary<char, List<string>> pools = new();
        var self = lemma.ToLowerInvariant();

        void Add(char pos, IEnumerable<string> words)
        {
            if (!pools.TryGetValue(pos, out var pool))
                pools[pos] = pool = [];
            foreach (var word in words)
                if (!word.Equals(self, StringComparison.OrdinalIgnoreCase))
                    pool.Add(word);
        }

        foreach (var (_, offsets) in entries)
            foreach (var offset in offsets)
                foreach (var pointer in data.TryGetValue(offset, out var synset) ? synset.Pointers : [])
                {
                    if (pointer.Symbol != '!' || !data.TryGetValue(pointer.Offset, out var antonym)) continue;
                    Add(synset!.Pos, antonym.Lemmas);
                    if (antonym.Pos is not ('a' or 's')) continue;
                    foreach (var satellite in antonym.Pointers.Where(p => p.Symbol == '&'))
                        if (data.TryGetValue(satellite.Offset, out var target))
                            Add(synset.Pos, target.Lemmas);
                }
        return pools;
    }

    private static string DisplayPos(char pos) => pos switch
    {
        'n' => "n.",
        'v' => "v.",
        'a' or 's' => "adj.",
        'r' => "adv.",
        _ => "",
    };

    /// <summary>WordNet lemmas use "_" for spaces and carry trailing markers like "not_bad(p)".</summary>
    private static string Normalize(string lemma) =>
        MarkerRegex.Replace(lemma.Replace('_', ' '), "").Trim();

    /// <summary>index.&lt;pos&gt; lines: lemma pos synset_cnt p_cnt [ptr symbols] sense_cnt tagsense_cnt offsets…</summary>
    private static Dictionary<string, List<(char Pos, int[] Offsets)>> ReadIndex(string dir)
    {
        Dictionary<string, List<(char, int[])>> index = new(StringComparer.Ordinal);
        foreach (var file in PosFiles)
        {
            var path = Path.Combine(dir, $"index.{file}");
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith(' ')) continue;
                var tokens = line.Split(' ');
                if (tokens.Length < 9) continue;
                var lemma = Normalize(tokens[0]);
                var pos = tokens[1][0];
                var synsetCount = int.Parse(tokens[2], CultureInfo.InvariantCulture);
                var pointerCount = int.Parse(tokens[3], CultureInfo.InvariantCulture);
                var start = 6 + pointerCount;
                if (tokens.Length < start + synsetCount) continue;
                var offsets = new int[synsetCount];
                for (var i = 0; i < synsetCount; i++)
                    offsets[i] = int.Parse(tokens[start + i], CultureInfo.InvariantCulture);
                if (!index.TryGetValue(lemma, out var entries))
                    index[lemma] = entries = [];
                entries.Add((pos, offsets));
            }
        }
        return index;
    }

    /// <summary>
    /// data.&lt;pos&gt; lines: offset lexnum ss_type w_cnt(HEX) (word lex_id)…
    /// p_cnt (DEC) (symbol offset pos source_target)… [frames] | gloss.
    /// Parsing stops after the pointers; frames and gloss are ignored.
    /// </summary>
    private static Dictionary<int, Synset> ReadSynsets(string dir)
    {
        Dictionary<int, Synset> data = new();
        foreach (var file in PosFiles)
        {
            var path = Path.Combine(dir, $"data.{file}");
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith(' ')) continue;
                var tokens = line.Split(' ');
                if (tokens.Length < 5 || !int.TryParse(tokens[0], out var offset)) continue;
                var pos = tokens[2][0];
                var wordCount = int.Parse(tokens[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                List<string> lemmas = new(wordCount);
                for (var i = 0; i < wordCount; i++)
                    lemmas.Add(Normalize(tokens[4 + i * 2]));

                var index = 4 + wordCount * 2;
                if (tokens.Length <= index || !int.TryParse(tokens[index], out var pointerCount)) continue;
                List<Pointer> pointers = new(pointerCount);
                index++;
                for (var i = 0; i < pointerCount && index + 3 < tokens.Length; i++)
                {
                    var symbol = tokens[index];
                    if (!int.TryParse(tokens[index + 1], out var target)) break;
                    pointers.Add(new Pointer(symbol[0], target, tokens[index + 2][0]));
                    index += 4;
                }
                data[offset] = new Synset(pos, lemmas, pointers);
            }
        }
        return data;
    }
}
