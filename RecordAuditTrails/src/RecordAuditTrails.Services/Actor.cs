using AuthenticationCore.Shared.Roles;

namespace RecordAuditTrails.Services;

/// <summary>
/// The authenticated operator, taken from the AuthenticationCore access token. It is never read from a request
/// body, so an operator cannot attribute an action to somebody else.
/// </summary>
public sealed record Actor(string UserId, string Username, UserRole Role);
