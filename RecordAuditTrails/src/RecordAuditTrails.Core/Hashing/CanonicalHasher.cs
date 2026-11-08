using System.Security.Cryptography;
using System.Text;

namespace RecordAuditTrails.Core.Hashing;

/// <summary>Deterministic SHA-256 hashing of ordered field lists, unambiguous across null/empty values and separators.</summary>
public static class CanonicalHasher
{
    /// <summary>Previous-hash value of the first link in any chain.</summary>
    public static readonly string GenesisHash = new('0', 64);

    public static string Hash(params string?[] parts)
    {
        var canonical = new StringBuilder();
        foreach (var part in parts)
        {
            // Length prefix makes ("ab","c") differ from ("a","bc"); -1 distinguishes null from "".
            canonical.Append(part is null ? "-1" : part.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(':')
                .Append(part)
                .Append('|');
        }
        return Sha256Hex(canonical.ToString());
    }

    public static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
