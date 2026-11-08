namespace AuthenticationCore.Shared.Claims;

public static class AuthClaimTypes
{
    public const string Subject = "sub";
    public const string TokenId = "jti";
    public const string IssuedAt = "iat";
    public const string Username = "preferred_username";
    public const string Role = "role";
    public const string CorrelationId = "cid";
}
