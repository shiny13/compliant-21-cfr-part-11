using System.Security.Cryptography;
using AuthenticationCore.Shared.Keys;
using Microsoft.IdentityModel.Tokens;

namespace AuthenticationCore.Service.Security;

/// <summary>The auth service's RSA key pair. Only this service ever holds the private key.</summary>
public sealed class ServiceSigningKey
{
    private ServiceSigningKey(RSA rsa, bool ephemeral)
    {
        Rsa = rsa;
        Ephemeral = ephemeral;
        KeyId = KeyIdentifier.FromRsa(rsa);
        SecurityKey = new RsaSecurityKey(rsa) { KeyId = KeyId };
        PublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();
    }

    public RSA Rsa { get; }
    public bool Ephemeral { get; }
    public string KeyId { get; }
    public RsaSecurityKey SecurityKey { get; }
    public string PublicKeyPem { get; }

    public static ServiceSigningKey LoadOrCreate(string? pemPath)
    {
        var rsa = RSA.Create(2048);
        if (!string.IsNullOrWhiteSpace(pemPath))
        {
            rsa.ImportFromPem(File.ReadAllText(pemPath));
            return new ServiceSigningKey(rsa, ephemeral: false);
        }
        return new ServiceSigningKey(rsa, ephemeral: true);
    }

    public object ToJwk()
    {
        var p = Rsa.ExportParameters(includePrivateParameters: false);
        return new
        {
            kty = "RSA",
            use = "sig",
            alg = SecurityAlgorithms.RsaSha256,
            kid = KeyId,
            n = Base64UrlEncoder.Encode(p.Modulus),
            e = Base64UrlEncoder.Encode(p.Exponent),
        };
    }
}
