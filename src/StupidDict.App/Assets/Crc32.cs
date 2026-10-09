namespace StupidDict.App.Assets;

/// <summary>
/// Classic CRC-32 (IEEE, reflected, polynomial 0xEDB88320) — the checksum a
/// zip central directory carries per entry. Hand-rolled because
/// System.IO.Compression does not verify entry CRCs while streaming
/// (verified on .NET 10: a corrupted-but-decodable entry reads back
/// silently), yet MainWindow's extraction leans on exactly that check as
/// the integrity backstop for checksum-less reuse; System.IO.Hashing would
/// add a package dependency for a thirty-line table. The known-answer test
/// ("123456789" → CBF43926) plus the zip roundtrip cases pin the algorithm.
/// </summary>
internal static class Crc32
{
    // Running-state convention: start a stream at InitialState, feed every
    // chunk through Update, then finish with Value — the published form the
    // zip format stores.
    public const uint InitialState = 0xFFFFFFFFu;

    private static readonly uint[] Table = BuildTable();

    public static uint Update(uint state, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            state = (state >> 8) ^ Table[(state ^ b) & 0xFF];
        return state;
    }

    public static uint Value(uint state) => ~state;

    public static uint Compute(ReadOnlySpan<byte> data) => Value(Update(InitialState, data));

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (var i = 0; i < table.Length; i++)
        {
            var value = (uint)i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }
}
