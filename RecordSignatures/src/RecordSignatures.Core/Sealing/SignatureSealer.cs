using Microsoft.Extensions.Options;
using RecordSignatures.Core.Models;

namespace RecordSignatures.Core.Sealing;

/// <summary>
/// Seals a signature with an HMAC-SHA256 over every field that gives it meaning, including the hash of the signed record
/// content and the previous signature's seal. Changing, copying to another record, or removing a signature breaks the seal
/// or the chain, and forging one needs the secret key, not just database access (11.70).
/// </summary>
public sealed class SignatureSealer
{
    public const int MinimumKeyBytes = 32;

    private readonly Dictionary<string, byte[]> _keys;
    private readonly string _activeKeyId;

    public SignatureSealer(IOptions<SignatureOptions> options)
    {
        var settings = options.Value;
        if (settings.SealKeys.Count == 0)
        {
            throw new InvalidOperationException("Signatures:SealKeys must contain at least one key.");
        }

        _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (id, base64) in settings.SealKeys)
        {
            byte[] key;
            try
            {
                key = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                throw new InvalidOperationException($"Seal key '{id}' is not valid base64.");
            }
            if (key.Length < MinimumKeyBytes)
            {
                throw new InvalidOperationException($"Seal key '{id}' must be at least {MinimumKeyBytes} bytes.");
            }
            _keys[id] = key;
        }

        _activeKeyId = settings.ActiveSealKeyId ?? "";
        if (!_keys.ContainsKey(_activeKeyId))
        {
            throw new InvalidOperationException("Signatures:ActiveSealKeyId must name one of Signatures:SealKeys.");
        }
    }

    public string ActiveKeyId => _activeKeyId;

    /// <summary>Computes the seal for a signature using the key named by <paramref name="keyId"/> (the active key when null).</summary>
    public string Compute(SignatureRecord s, string? keyId = null)
    {
        var key = _keys.TryGetValue(keyId ?? _activeKeyId, out var found) ? found : null;
        return key is null ? "" : CanonicalHasher.HmacSha256(key, Canonical(s));
    }

    /// <summary>True when the stored seal matches the signature contents under the key it names.</summary>
    public bool Verify(SignatureRecord s)
    {
        if (!_keys.ContainsKey(s.SealKeyId))
        {
            return false;
        }
        return CanonicalHasher.FixedTimeEquals(Compute(s, s.SealKeyId), s.SealHash);
    }

    private static IEnumerable<string?> Canonical(SignatureRecord s) =>
    [
        "RSSIG1",
        s.Id.ToString("D"),
        s.RecordId.ToString("D"),
        s.RecordVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
        s.RecordContentHash,
        s.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
        s.SignerUserId,
        s.SignerUsername,
        s.SignerPrintedName,
        s.MeaningCode,
        s.MeaningText,
        s.Comment,
        TimestampPrecision.Canonical(s.SignedUtc),
        s.PreviousSealHash,
        s.SealKeyId,
    ];
}
