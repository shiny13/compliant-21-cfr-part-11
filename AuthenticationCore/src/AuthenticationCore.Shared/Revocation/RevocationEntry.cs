namespace AuthenticationCore.Shared.Revocation;

public enum RevocationKind
{
    /// <summary>A single access token, identified by jti.</summary>
    Token = 0,

    /// <summary>Every access token of a subject issued at or before <see cref="RevocationEntry.RevokedBeforeUnix"/>.</summary>
    Subject = 1,
}

public sealed record RevocationEntry(
    long Sequence,
    RevocationKind Kind,
    string Key,
    long RevokedBeforeUnix,
    long ExpiresUnix);

public sealed record RevocationFeedResponse(long Latest, IReadOnlyList<RevocationEntry> Entries);
