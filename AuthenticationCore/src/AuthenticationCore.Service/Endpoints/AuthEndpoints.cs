using System.Security.Cryptography;
using System.Text;
using AuthenticationCore.Service.Auditing;
using AuthenticationCore.Service.Options;
using AuthenticationCore.Service.Revocation;
using AuthenticationCore.Service.Tokens;
using AuthenticationCore.Service.Users;
using AuthenticationCore.Shared.Authorization;
using AuthenticationCore.Shared.Claims;
using AuthenticationCore.Shared.Options;
using AuthenticationCore.Shared.Revocation;
using Microsoft.Extensions.Options;

namespace AuthenticationCore.Service.Endpoints;

public sealed record LoginRequest(string Username, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string? RefreshToken);

public sealed record RevokeRequest(string? TokenId, Guid? UserId);

public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn, string RefreshToken);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/.well-known/jwks.json", (HttpContext http, Security.ServiceSigningKey key) =>
        {
            http.Response.Headers.CacheControl = "public, max-age=300";
            return Results.Json(new { keys = new[] { key.ToJwk() } });
        }).AllowAnonymous();

        var auth = app.MapGroup("/auth");
        auth.MapPost("/login", LoginAsync).AllowAnonymous();
        auth.MapPost("/refresh", RefreshAsync).AllowAnonymous();
        auth.MapPost("/logout", Logout).RequireAuthorization();
        auth.MapGet("/me", Me).RequireAuthorization();
        auth.MapPost("/revoke", Revoke).RequireAuthorization(RolePolicies.Administrator);
        auth.MapGet("/revocations", Feed).AllowAnonymous();
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        UserAuthenticator authenticator,
        AccessTokenIssuer accessTokens,
        RefreshTokenService refreshTokens,
        IOptions<AuthenticationCoreOptions> options)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return Results.BadRequest(new { error = "username and password are required" });
        }

        var result = await authenticator.AuthenticateAsync(request.Username, request.Password);
        if (result.User is not { } user)
        {
            // One generic answer for every failure so callers cannot tell why it failed.
            return Results.Problem("Invalid credentials.", statusCode: StatusCodes.Status401Unauthorized);
        }

        return Results.Ok(BuildResponse(user, accessTokens, refreshTokens.Issue(user.Id), options.Value));
    }

    private static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        IUserStore users,
        AccessTokenIssuer accessTokens,
        RefreshTokenService refreshTokens,
        RevocationService revocations,
        IAuditLog audit,
        IOptions<AuthenticationCoreOptions> options)
    {
        var unauthorized = Results.Problem("Invalid refresh token.", statusCode: StatusCodes.Status401Unauthorized);
        if (string.IsNullOrEmpty(request.RefreshToken))
        {
            return unauthorized;
        }

        var rotation = refreshTokens.Rotate(request.RefreshToken);
        switch (rotation.Status)
        {
            case RotationStatus.ReuseDetected:
                revocations.RevokeSubject(rotation.UserId.ToString());
                audit.Record("refresh", "reuse-detected", rotation.UserId.ToString(), "token family and access tokens revoked");
                return unauthorized;

            case RotationStatus.Invalid:
                audit.Record("refresh", "failure", detail: "unknown or expired token");
                return unauthorized;
        }

        // Roles are re-read here, so role changes and deactivation take effect within one access token lifetime.
        var user = await users.FindByIdAsync(rotation.UserId);
        if (user is null || !user.IsActive)
        {
            refreshTokens.RevokeAllFor(rotation.UserId);
            audit.Record("refresh", "failure", rotation.UserId.ToString(), "user missing or inactive");
            return unauthorized;
        }

        audit.Record("refresh", "success", user.Id.ToString());
        return Results.Ok(BuildResponse(user, accessTokens, rotation.NewToken!, options.Value));
    }

    private static IResult Logout(
        HttpContext http,
        LogoutRequest? request,
        RevocationService revocations,
        RefreshTokenService refreshTokens,
        IAuditLog audit)
    {
        var principal = http.User;
        if (principal.GetTokenId() is { } tokenId)
        {
            revocations.RevokeToken(tokenId);
        }

        if (!string.IsNullOrEmpty(request?.RefreshToken) && Guid.TryParse(principal.GetSubject(), out var userId))
        {
            refreshTokens.RevokeFamilyOf(request.RefreshToken, userId);
        }

        audit.Record("logout", "success", principal.GetSubject());
        return Results.NoContent();
    }

    private static IResult Me(HttpContext http)
    {
        var user = http.User;
        return Results.Ok(new
        {
            subject = user.GetSubject(),
            username = user.GetUsername(),
            roles = user.GetRoles(),
            highestRole = user.GetHighestRole()?.ToString(),
            tokenId = user.GetTokenId(),
        });
    }

    private static IResult Revoke(
        HttpContext http,
        RevokeRequest request,
        RevocationService revocations,
        RefreshTokenService refreshTokens,
        IAuditLog audit)
    {
        if (request.TokenId is null && request.UserId is null)
        {
            return Results.BadRequest(new { error = "tokenId or userId is required" });
        }

        var actor = http.User.GetSubject();
        if (request.TokenId is { } tokenId)
        {
            revocations.RevokeToken(tokenId);
            audit.Record("revoke-token", "success", actor, tokenId);
        }

        if (request.UserId is { } userId)
        {
            revocations.RevokeSubject(userId.ToString());
            refreshTokens.RevokeAllFor(userId);
            audit.Record("revoke-user", "success", actor, userId.ToString());
        }
        return Results.NoContent();
    }

    private static IResult Feed(
        HttpContext http,
        RevocationLog log,
        IOptions<AuthServiceOptions> options,
        long since = 0)
    {
        var expected = options.Value.RevocationFeedServiceKey;
        if (string.IsNullOrEmpty(expected))
        {
            return Results.NotFound();
        }

        var presented = http.Request.Headers[RevocationSyncService.ServiceKeyHeader].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected)))
        {
            return Results.Unauthorized();
        }

        return Results.Ok(log.GetSince(since));
    }

    private static TokenResponse BuildResponse(
        UserAccount user,
        AccessTokenIssuer accessTokens,
        string refreshToken,
        AuthenticationCoreOptions options)
    {
        var issued = accessTokens.Issue(user);
        return new TokenResponse(issued.Token, "Bearer", (int)options.AccessTokenTtl.TotalSeconds, refreshToken);
    }
}
