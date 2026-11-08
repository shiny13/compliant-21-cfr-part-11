using System.Security.Cryptography;
using AuthenticationCore.Service.Options;
using Microsoft.Extensions.Options;

namespace AuthenticationCore.Service.Tokens;

public enum RotationStatus
{
    Rotated,
    Invalid,

    /// <summary>An already-used refresh token was presented again: the token family is considered stolen.</summary>
    ReuseDetected,
}

public sealed record RotationResult(RotationStatus Status, Guid UserId = default, string? NewToken = null);

/// <summary>
/// Opaque, single-use refresh tokens with rotation. Only a SHA-256 hash of each token is stored.
/// </summary>
public sealed class RefreshTokenService(IOptions<AuthServiceOptions> options, TimeProvider clock)
{
    private sealed class Record(Guid userId, Guid familyId, DateTimeOffset expiresAt)
    {
        public Guid UserId { get; } = userId;
        public Guid FamilyId { get; } = familyId;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public bool Consumed { get; set; }
    }

    private readonly Dictionary<string, Record> _records = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public string Issue(Guid userId) => Create(userId, Guid.NewGuid());

    public RotationResult Rotate(string token)
    {
        var hash = Hash(token);
        lock (_gate)
        {
            PruneExpired();

            if (!_records.TryGetValue(hash, out var record))
            {
                return new RotationResult(RotationStatus.Invalid);
            }

            if (record.Consumed)
            {
                RemoveFamily(record.FamilyId);
                return new RotationResult(RotationStatus.ReuseDetected, record.UserId);
            }

            record.Consumed = true;
            return new RotationResult(RotationStatus.Rotated, record.UserId, Create(record.UserId, record.FamilyId));
        }
    }

    /// <summary>Invalidates the family the token belongs to, provided it belongs to <paramref name="userId"/>.</summary>
    public bool RevokeFamilyOf(string token, Guid userId)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(Hash(token), out var record) || record.UserId != userId)
            {
                return false;
            }
            RemoveFamily(record.FamilyId);
            return true;
        }
    }

    public void RevokeAllFor(Guid userId)
    {
        lock (_gate)
        {
            foreach (var key in _records.Where(r => r.Value.UserId == userId).Select(r => r.Key).ToList())
            {
                _records.Remove(key);
            }
        }
    }

    private string Create(Guid userId, Guid familyId)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (_gate)
        {
            _records[Hash(token)] = new Record(userId, familyId, clock.GetUtcNow() + options.Value.RefreshTokenTtl);
        }
        return token;
    }

    private void RemoveFamily(Guid familyId)
    {
        foreach (var key in _records.Where(r => r.Value.FamilyId == familyId).Select(r => r.Key).ToList())
        {
            _records.Remove(key);
        }
    }

    private void PruneExpired()
    {
        var now = clock.GetUtcNow();
        foreach (var key in _records.Where(r => r.Value.ExpiresAt <= now).Select(r => r.Key).ToList())
        {
            _records.Remove(key);
        }
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
}
