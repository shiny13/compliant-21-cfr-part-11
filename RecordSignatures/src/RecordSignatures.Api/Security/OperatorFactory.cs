using System.Security.Claims;
using AuthenticationCore.Shared.Claims;
using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Models;

namespace RecordSignatures.Api.Security;

public static class OperatorFactory
{
    /// <summary>
    /// Builds the acting individual from the validated AuthenticationCore token: user id, username and role come only
    /// from signed claims. The raw token is kept to forward to RecordAuditTrails.
    /// </summary>
    public static Operator FromHttpContext(HttpContext http)
    {
        var principal = http.User;
        var subject = principal.GetSubject();
        var username = principal.GetUsername();
        var role = principal.GetHighestRole();
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(username) || role is null)
        {
            throw new ForbiddenException("The access token does not identify a user with a valid role.");
        }

        var header = http.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        var token = header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..].Trim() : "";
        if (token.Length == 0)
        {
            throw new ForbiddenException("The bearer token could not be read.");
        }
        return new Operator(subject, username, role.Value, token);
    }
}
