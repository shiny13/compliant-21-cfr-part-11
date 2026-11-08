namespace AuthenticationCore.Shared.Options;

public sealed class AuthenticationCoreOptions
{
    public const string SectionName = "AuthenticationCore";

    public string Issuer { get; set; } = "https://auth.local";
    public string Audience { get; set; } = "authentication-core-api";

    /// <summary>Access token lifetime. Keep short: it bounds the exposure window of a revoked token.</summary>
    public TimeSpan AccessTokenTtl { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Static RSA public key (SPKI PEM). Enables fully offline validation.</summary>
    public string? PublicKeyPem { get; set; }

    /// <summary>JWKS endpoint of the auth service. Keys are fetched in the background and cached in memory.</summary>
    public string? JwksUrl { get; set; }

    public TimeSpan JwksRefreshInterval { get; set; } = TimeSpan.FromMinutes(5);

    public RevocationFeedOptions Revocation { get; set; } = new();

    public InternalContextOptions InternalContext { get; set; } = new();
}

public sealed class RevocationFeedOptions
{
    /// <summary>Revocation feed endpoint of the auth service. When unset, no polling occurs.</summary>
    public string? FeedUrl { get; set; }

    public string? FeedServiceKey { get; set; }

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);
}

public sealed class InternalContextOptions
{
    public const string DefaultHeaderName = "X-Internal-Context";

    /// <summary>Base64 symmetric key (min 32 bytes) shared between the gateway and internal services.</summary>
    public string? SigningKey { get; set; }

    public string Issuer { get; set; } = "gateway";
    public string Audience { get; set; } = "internal-services";
    public string HeaderName { get; set; } = DefaultHeaderName;
    public TimeSpan Ttl { get; set; } = TimeSpan.FromSeconds(30);
}
