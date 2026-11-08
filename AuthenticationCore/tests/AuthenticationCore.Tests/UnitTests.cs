using AuthenticationCore.Service.Security;
using AuthenticationCore.Shared.Revocation;
using AuthenticationCore.Shared.Roles;
using AuthenticationCore.Tests.Support;

namespace AuthenticationCore.Tests;

public class RoleTests
{
    [Fact]
    public void Roles_are_ordered_with_one_as_the_highest_privilege()
    {
        Assert.Equal(1, (int)UserRole.Administrator);
        Assert.Equal(2, (int)UserRole.LabManager);
        Assert.Equal(3, (int)UserRole.Reviewer);
        Assert.Equal(4, (int)UserRole.LabTechnician);
    }

    [Theory]
    [InlineData(UserRole.Administrator, UserRole.LabTechnician, true)]
    [InlineData(UserRole.Reviewer, UserRole.Reviewer, true)]
    [InlineData(UserRole.Reviewer, UserRole.LabManager, false)]
    [InlineData(UserRole.LabTechnician, UserRole.Administrator, false)]
    public void Satisfies_allows_equal_or_higher_privilege(UserRole actual, UserRole required, bool expected) =>
        Assert.Equal(expected, actual.Satisfies(required));

    [Fact]
    public void HighestOf_picks_the_most_privileged_valid_role_and_ignores_unknown_values()
    {
        Assert.Equal(UserRole.LabManager, UserRoleExtensions.HighestOf(["LabTechnician", "bogus", "LabManager"]));
        Assert.Null(UserRoleExtensions.HighestOf(["bogus", "administrator"]));
    }
}

public class RevocationCacheTests
{
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Revoked_token_is_rejected_until_its_retention_ends()
    {
        var cache = new InMemoryRevocationCache(_clock);
        cache.RevokeToken("jti-1", _clock.GetUtcNow().AddMinutes(5));

        Assert.True(cache.IsRevoked("jti-1", "user", _clock.GetUtcNow()));
        Assert.False(cache.IsRevoked("jti-2", "user", _clock.GetUtcNow()));

        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(cache.IsRevoked("jti-1", "user", _clock.GetUtcNow()));
    }

    [Fact]
    public void Subject_revocation_only_covers_tokens_issued_before_the_cutoff()
    {
        var cache = new InMemoryRevocationCache(_clock);
        var cutoff = _clock.GetUtcNow();
        cache.RevokeSubject("user", cutoff, cutoff.AddMinutes(5));

        Assert.True(cache.IsRevoked("any", "user", cutoff.AddSeconds(-30)));
        Assert.True(cache.IsRevoked("any", "user", cutoff));
        Assert.False(cache.IsRevoked("any", "user", cutoff.AddSeconds(1)));
        Assert.False(cache.IsRevoked("any", "someone-else", cutoff.AddSeconds(-30)));
    }

    [Fact]
    public void Prune_removes_only_expired_entries()
    {
        var cache = new InMemoryRevocationCache(_clock);
        cache.RevokeToken("short", _clock.GetUtcNow().AddMinutes(1));
        cache.RevokeToken("long", _clock.GetUtcNow().AddMinutes(10));
        cache.RevokeSubject("user", _clock.GetUtcNow(), _clock.GetUtcNow().AddMinutes(1));

        _clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(2, cache.PruneExpired());
        Assert.True(cache.IsRevoked("long", null, _clock.GetUtcNow()));
    }

    [Fact]
    public void Feed_entries_can_be_applied()
    {
        var cache = new InMemoryRevocationCache(_clock);
        var expires = _clock.GetUtcNow().AddMinutes(5).ToUnixTimeSeconds();

        cache.Apply(new RevocationEntry(1, RevocationKind.Token, "jti-9", 0, expires));

        Assert.True(cache.IsRevoked("jti-9", null, _clock.GetUtcNow()));
    }
}

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void Correct_password_verifies_and_wrong_password_does_not()
    {
        var hash = _hasher.Hash("correct horse");

        Assert.True(_hasher.Verify("correct horse", hash));
        Assert.False(_hasher.Verify("wrong horse", hash));
        Assert.False(_hasher.Verify("correct horse", "not-a-hash"));
    }

    [Fact]
    public void Same_password_hashes_differently_each_time()
    {
        Assert.NotEqual(_hasher.Hash("pw"), _hasher.Hash("pw"));
    }
}
