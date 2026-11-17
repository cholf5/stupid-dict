using System.Security.Cryptography;

namespace StupidDict.App.Update;

/// <summary>
/// Update-package integrity (SHA-256). Why it is mandatory: downloads go
/// through three public mirror prefixes, so checking only "Content-Length
/// complete + payload magic" would let a hijacked mirror hand us any zip that
/// starts with PK — and in a self-update that means replacing the program the
/// user is running. CI emits <c>&lt;asset&gt;.sha256</c> per asset; the app
/// fetches it first and verifies with it.
/// </summary>
internal static class UpdateChecksum
{
    /// <summary>The file's SHA-256 as lowercase hex (same shape as <c>sha256sum</c> output).</summary>
    internal static string ComputeHex(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// Parses a checksum file: accepts <c>&lt;hex&gt;</c> and
    /// <c>&lt;hex&gt;  &lt;file name&gt;</c> (the latter is what
    /// <c>sha256sum</c> / <c>shasum -a 256</c> print by default, which CI uses).
    /// Hex is case-insensitive. Anything malformed returns false — the caller
    /// refuses to install, never "close enough, let it through".
    /// </summary>
    internal static bool TryParse(string? text, out string hex)
    {
        hex = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var firstToken = text.TrimStart().Split([' ', '\t', '\r', '\n'], 2)[0];
        if (firstToken.Length != 64)
        {
            return false;
        }

        foreach (var c in firstToken)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        hex = firstToken.ToLowerInvariant();
        return true;
    }

    /// <summary>
    /// Verifies the downloaded asset. A mismatch throws
    /// <see cref="InvalidDataException"/>; the caller deletes the file — it may
    /// be an attack payload and must not sit in a folder where a user could
    /// double-click it.
    /// </summary>
    internal static void Verify(string filePath, string expectedHex, string assetName)
    {
        var actual = ComputeHex(filePath);
        if (!string.Equals(actual, expectedHex, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"sha256 mismatch for {assetName}: expected {expectedHex}, got {actual}");
        }
    }
}
