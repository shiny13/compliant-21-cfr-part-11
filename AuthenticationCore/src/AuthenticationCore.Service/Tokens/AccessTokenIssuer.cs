using System.Security.Claims;
using AuthenticationCore.Service.Security;
using AuthenticationCore.Service.Users;
using AuthenticationCore.Shared.Claims;
using AuthenticationCore.Shared.Options;
using AuthenticationCore.Shared.Roles;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthenticationCore.Service.Tokens;

public sealed record IssuedAccessToken(string Token, string TokenId, DateTimeOffset ExpiresAt);

public sealed class AccessTokenIssuer(ServiceSigningKey key, IOptions<AuthenticationCoreOptions> options, TimeProvider clock)
{
    private readonly JsonWebTokenHandler _handler = new();

    public IssuedAccessToken Issue(UserAccount user)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow();
        var expires = now + settings.AccessTokenTtl;
        var tokenId = Guid.NewGuid().ToString("N");

        var claims = new List<Claim>
        {
            new(AuthClaimTypes.Subject, user.Id.ToString()),
            new(AuthClaimTypes.Username, user.Username),
            new(AuthClaimTypes.TokenId, tokenId),
        };
        claims.AddRange(user.Roles.Select(r => new Claim(AuthClaimTypes.Role, r.ToClaimValue())));

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(key.SecurityKey, SecurityAlgorithms.RsaSha256),
        });

        return new IssuedAccessToken(token, tokenId, expires);
    }
}
