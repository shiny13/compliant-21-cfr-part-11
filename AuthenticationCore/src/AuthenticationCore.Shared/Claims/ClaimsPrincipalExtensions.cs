using System.Globalization;
using System.Security.Claims;
using AuthenticationCore.Shared.Roles;

namespace AuthenticationCore.Shared.Claims;

public static class ClaimsPrincipalExtensions
{
    public static string? GetSubject(this ClaimsPrincipal principal) => principal.FindFirstValue(AuthClaimTypes.Subject);

    public static string? GetTokenId(this ClaimsPrincipal principal) => principal.FindFirstValue(AuthClaimTypes.TokenId);

    public static string? GetUsername(this ClaimsPrincipal principal) => principal.FindFirstValue(AuthClaimTypes.Username);

    public static DateTimeOffset? GetIssuedAt(this ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirstValue(AuthClaimTypes.IssuedAt), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;

    public static IReadOnlyList<string> GetRoles(this ClaimsPrincipal principal) =>
        principal.FindAll(AuthClaimTypes.Role).Select(c => c.Value).ToArray();

    public static UserRole? GetHighestRole(this ClaimsPrincipal principal) =>
        UserRoleExtensions.HighestOf(principal.FindAll(AuthClaimTypes.Role).Select(c => c.Value));
}
