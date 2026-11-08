using System.Security.Cryptography;
using System.Text;

namespace RecordSignatures.Core.Sealing;

/// <summary>
/// Turns an ordered list of values into bytes so that no two different lists give the same bytes: each value is
/// length-prefixed and null is distinct from empty. Prevents field-boundary collisions ("ab","c" vs "a","bc").
/// </summary>
public static class CanonicalHasher
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    public static byte[] Encode(IEnumerable<string?> parts)
    {
        using var ms = new MemoryStream();
        foreach (var part in parts)
        {
            if (part is null)
            {
                ms.WriteByte(0);
                continue;
            }
            var bytes = Encoding.UTF8.GetBytes(part);
            ms.WriteByte(1);
            ms.Write(BitConverter.GetBytes(bytes.Length));
            ms.Write(bytes);
        }
        return ms.ToArray();
    }

    public static string Sha256(IEnumerable<string?> parts) => Convert.ToHexStringLower(SHA256.HashData(Encode(parts)));

    public static string HmacSha256(byte[] key, IEnumerable<string?> parts) => Convert.ToHexStringLower(HMACSHA256.HashData(key, Encode(parts)));

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
