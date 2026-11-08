using System.Security.Cryptography;
using AuthenticationCore.Shared.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuthenticationCore.Shared.Keys;

public static class KeyIdentifier
{
    /// <summary>Deterministic key id derived from the public key, so issuer and validators agree without coordination.</summary>
    public static string FromRsa(RSA rsa)
    {
        var hash = SHA256.HashData(rsa.ExportSubjectPublicKeyInfo());
        return Base64UrlEncoder.Encode(hash, 0, 16);
    }
}

/// <summary>In-memory set of trusted public keys. Token validation reads this and never touches the network.</summary>
public interface ISigningKeyStore
{
    bool HasKeys { get; }

    IReadOnlyList<SecurityKey> Resolve(string? keyId);

    void Replace(IEnumerable<SecurityKey> keys);
}

public sealed class SigningKeyStore : ISigningKeyStore
{
    private volatile SecurityKey[] _keys = [];

    public SigningKeyStore(IOptions<AuthenticationCoreOptions> options)
    {
        var pem = options.Value.PublicKeyPem;
        if (!string.IsNullOrWhiteSpace(pem))
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            _keys = [new RsaSecurityKey(rsa) { KeyId = KeyIdentifier.FromRsa(rsa) }];
        }
    }

    public bool HasKeys => _keys.Length > 0;

    public IReadOnlyList<SecurityKey> Resolve(string? keyId)
    {
        var snapshot = _keys;
        if (keyId is null)
        {
            return snapshot;
        }

        // Unknown kid fails closed: no key, no validation.
        return snapshot.Where(k => string.Equals(k.KeyId, keyId, StringComparison.Ordinal)).ToArray();
    }

    public void Replace(IEnumerable<SecurityKey> keys)
    {
        var next = keys.ToArray();
        if (next.Length > 0)
        {
            _keys = next;
        }
    }
}
