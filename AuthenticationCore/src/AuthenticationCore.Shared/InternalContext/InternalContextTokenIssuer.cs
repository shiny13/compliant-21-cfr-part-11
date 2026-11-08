using System.Security.Claims;
using AuthenticationCore.Shared.Claims;
using AuthenticationCore.Shared.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthenticationCore.Shared.InternalContext;

/// <summary>
/// Used by the API gateway after it has validated the external JWT. Mints a lightweight, very short-lived
/// token carrying only the hydrated user context for downstream services.
/// </summary>
public sealed class InternalContextTokenIssuer(IOptions<AuthenticationCoreOptions> options, TimeProvider clock)
{
    private readonly JsonWebTokenHandler _handler = new();

    public string Issue(ClaimsPrincipal externalUser, string? correlationId = null)
    {
        var settings = options.Value.InternalContext;
        var key = InternalContextKeys.Create(settings);
        var now = clock.GetUtcNow().UtcDateTime;

        var claims = new List<Claim> { new(AuthClaimTypes.Subject, externalUser.GetSubject() ?? throw new InvalidOperationException("User has no subject.")) };
        if (externalUser.GetUsername() is { } username)
        {
            claims.Add(new Claim(AuthClaimTypes.Username, username));
        }
        claims.AddRange(externalUser.GetRoles().Select(r => new Claim(AuthClaimTypes.Role, r)));
        claims.Add(new Claim(AuthClaimTypes.CorrelationId, correlationId ?? Guid.NewGuid().ToString("N")));

        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + settings.Ttl,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
    }
}

internal static class InternalContextKeys
{
    public static SymmetricSecurityKey Create(InternalContextOptions settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SigningKey))
        {
            throw new InvalidOperationException("AuthenticationCore:InternalContext:SigningKey is not configured.");
        }

        var bytes = Convert.FromBase64String(settings.SigningKey);
        if (bytes.Length < 32)
        {
            throw new InvalidOperationException("Internal context signing key must be at least 32 bytes.");
        }
        return new SymmetricSecurityKey(bytes);
    }
}
