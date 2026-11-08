using System.Security.Claims;
using AuthenticationCore.Shared.Claims;
using RecordAuditTrails.Core.Exceptions;
using RecordAuditTrails.Services;

namespace RecordAuditTrails.Api.Security;

public static class ActorFactory
{
    /// <summary>
    /// Builds the operator identity from the validated AuthenticationCore token. The user id, username and role come
    /// only from signed claims.
    /// </summary>
    public static Actor FromPrincipal(ClaimsPrincipal principal)
    {
        var subject = principal.GetSubject();
        var username = principal.GetUsername();
        var role = principal.GetHighestRole();
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(username) || role is null)
        {
            throw new AccessDeniedException("The access token does not identify a user with a valid role.");
        }
        return new Actor(subject, username, role.Value);
    }
}
