using System.Collections.Concurrent;

namespace AuthenticationCore.Shared.Revocation;

/// <summary>
/// Entries only need to live as long as the tokens they cover (short TTL), so memory stays bounded.
/// </summary>
public sealed class InMemoryRevocationCache(TimeProvider clock) : IRevocationCache
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset RevokedBefore, DateTimeOffset RetainUntil)> _subjects =
        new(StringComparer.Ordinal);

    public bool IsRevoked(string? tokenId, string? subject, DateTimeOffset issuedAt)
    {
        var now = clock.GetUtcNow();

        if (tokenId is not null && _tokens.TryGetValue(tokenId, out var retainUntil) && retainUntil > now)
        {
            return true;
        }

        return subject is not null
            && _subjects.TryGetValue(subject, out var entry)
            && entry.RetainUntil > now
            && issuedAt <= entry.RevokedBefore;
    }

    public void RevokeToken(string tokenId, DateTimeOffset retainUntil) =>
        _tokens.AddOrUpdate(tokenId, retainUntil, (_, existing) => existing > retainUntil ? existing : retainUntil);

    public void RevokeSubject(string subject, DateTimeOffset revokedBefore, DateTimeOffset retainUntil) =>
        _subjects.AddOrUpdate(
            subject,
            (revokedBefore, retainUntil),
            (_, existing) => (
                existing.RevokedBefore > revokedBefore ? existing.RevokedBefore : revokedBefore,
                existing.RetainUntil > retainUntil ? existing.RetainUntil : retainUntil));

    public void Apply(RevocationEntry entry)
    {
        var expires = DateTimeOffset.FromUnixTimeSeconds(entry.ExpiresUnix);
        switch (entry.Kind)
        {
            case RevocationKind.Token:
                RevokeToken(entry.Key, expires);
                break;
            case RevocationKind.Subject:
                RevokeSubject(entry.Key, DateTimeOffset.FromUnixTimeSeconds(entry.RevokedBeforeUnix), expires);
                break;
        }
    }

    public int PruneExpired()
    {
        var now = clock.GetUtcNow();
        var removed = 0;
        foreach (var (key, until) in _tokens)
        {
            if (until <= now && _tokens.TryRemove(key, out _)) removed++;
        }
        foreach (var (key, entry) in _subjects)
        {
            if (entry.RetainUntil <= now && _subjects.TryRemove(key, out _)) removed++;
        }
        return removed;
    }
}
