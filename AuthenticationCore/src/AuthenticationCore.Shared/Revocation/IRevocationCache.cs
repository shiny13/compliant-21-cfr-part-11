namespace AuthenticationCore.Shared.Revocation;

/// <summary>Local, in-memory revocation lookup. Checks never leave the process.</summary>
public interface IRevocationCache
{
    bool IsRevoked(string? tokenId, string? subject, DateTimeOffset issuedAt);

    void RevokeToken(string tokenId, DateTimeOffset retainUntil);

    void RevokeSubject(string subject, DateTimeOffset revokedBefore, DateTimeOffset retainUntil);

    void Apply(RevocationEntry entry);

    int PruneExpired();
}
