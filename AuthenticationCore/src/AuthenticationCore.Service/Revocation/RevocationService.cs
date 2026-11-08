using AuthenticationCore.Shared.Options;
using AuthenticationCore.Shared.Revocation;
using Microsoft.Extensions.Options;

namespace AuthenticationCore.Service.Revocation;

/// <summary>Append-only feed that downstream services poll. Entries are dropped once they can no longer matter.</summary>
public sealed class RevocationLog(TimeProvider clock)
{
    private readonly List<RevocationEntry> _entries = [];
    private readonly Lock _gate = new();
    private long _sequence;

    public RevocationEntry Append(RevocationKind kind, string key, DateTimeOffset revokedBefore, DateTimeOffset expires)
    {
        lock (_gate)
        {
            var entry = new RevocationEntry(++_sequence, kind, key, revokedBefore.ToUnixTimeSeconds(), expires.ToUnixTimeSeconds());
            _entries.Add(entry);
            return entry;
        }
    }

    public RevocationFeedResponse GetSince(long since)
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        lock (_gate)
        {
            _entries.RemoveAll(e => e.ExpiresUnix <= now);
            return new RevocationFeedResponse(_sequence, _entries.Where(e => e.Sequence > since).ToArray());
        }
    }
}

public sealed class RevocationService(
    IRevocationCache cache,
    RevocationLog log,
    IOptions<AuthenticationCoreOptions> options,
    TimeProvider clock)
{
    /// <summary>Revokes one access token. It is retained only as long as a token could still be valid.</summary>
    public void RevokeToken(string tokenId)
    {
        var expires = RetainUntil();
        cache.RevokeToken(tokenId, expires);
        log.Append(RevocationKind.Token, tokenId, DateTimeOffset.UnixEpoch, expires);
    }

    /// <summary>
    /// Revokes every access token issued to the subject up to now. Tokens issued within the same second
    /// as the revocation are included, because token issue times have one-second resolution.
    /// </summary>
    public void RevokeSubject(string subject)
    {
        var revokedBefore = DateTimeOffset.FromUnixTimeSeconds(clock.GetUtcNow().ToUnixTimeSeconds());
        var expires = RetainUntil();
        cache.RevokeSubject(subject, revokedBefore, expires);
        log.Append(RevocationKind.Subject, subject, revokedBefore, expires);
    }

    private DateTimeOffset RetainUntil()
    {
        var settings = options.Value;
        return clock.GetUtcNow() + settings.AccessTokenTtl + settings.ClockSkew + TimeSpan.FromSeconds(1);
    }
}
