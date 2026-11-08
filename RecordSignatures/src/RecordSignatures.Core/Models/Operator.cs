using AuthenticationCore.Shared.Roles;

namespace RecordSignatures.Core.Models;

/// <summary>
/// The authenticated individual performing an action. Everything except <see cref="AccessToken"/> comes from claims of
/// the validated AuthenticationCore token, never from request bodies. The token is forwarded to RecordAuditTrails so the
/// audit trail records the same individual.
/// </summary>
public sealed class Operator(string userId, string username, UserRole role, string accessToken)
{
    public string UserId { get; } = userId;

    public string Username { get; } = username;

    public UserRole Role { get; } = role;

    public string AccessToken { get; } = accessToken;

    public override string ToString() => $"{Username} ({UserId}, {Role})";
}
