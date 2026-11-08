using AuthenticationCore.Shared.Roles;

namespace AuthenticationCore.Service.Options;

public sealed class AuthServiceOptions
{
    public const string SectionName = "AuthService";

    public TimeSpan RefreshTokenTtl { get; set; } = TimeSpan.FromHours(8);

    public int MaxFailedAttempts { get; set; } = 5;

    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>PEM file holding the RSA private key. When unset, an ephemeral key is generated at startup.</summary>
    public string? SigningKeyPemPath { get; set; }

    /// <summary>Shared secret that downstream services present to read the revocation feed. Feed is disabled when unset.</summary>
    public string? RevocationFeedServiceKey { get; set; }

    /// <summary>Development convenience only. Never configure seed users in production.</summary>
    public List<SeedUser> SeedUsers { get; set; } = [];
}

public sealed class SeedUser
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public List<UserRole> Roles { get; set; } = [];
}
