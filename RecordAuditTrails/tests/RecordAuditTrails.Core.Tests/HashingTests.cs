using RecordAuditTrails.Core.Hashing;

namespace RecordAuditTrails.Core.Tests;

public class HashingTests
{
    [Fact]
    public void Hash_IsDeterministic() =>
        Assert.Equal(CanonicalHasher.Hash("a", "b", null), CanonicalHasher.Hash("a", "b", null));

    [Fact]
    public void Hash_DistinguishesFieldBoundaries() =>
        Assert.NotEqual(CanonicalHasher.Hash("ab", "c"), CanonicalHasher.Hash("a", "bc"));

    [Fact]
    public void Hash_DistinguishesNullFromEmpty() =>
        Assert.NotEqual(CanonicalHasher.Hash("a", null), CanonicalHasher.Hash("a", ""));

    [Fact]
    public void Hash_ReturnsSixtyFourLowercaseHexCharacters()
    {
        var hash = CanonicalHasher.Hash("x");
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
    }

    [Fact]
    public void Sha256Hex_MatchesKnownVector() =>
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", CanonicalHasher.Sha256Hex("abc"));

    [Fact]
    public void GenesisHash_IsSixtyFourZeros() => Assert.Equal(new string('0', 64), CanonicalHasher.GenesisHash);

    [Fact]
    public void FixedTimeEquals_ComparesValues()
    {
        Assert.True(CanonicalHasher.FixedTimeEquals("abc", "abc"));
        Assert.False(CanonicalHasher.FixedTimeEquals("abc", "abd"));
        Assert.False(CanonicalHasher.FixedTimeEquals("abc", "abcd"));
    }

    [Fact]
    public void ToStorage_TruncatesToMicrosecondsAndForcesUtc()
    {
        var input = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)).AddTicks(1234567);
        var stored = TimestampPrecision.ToStorage(input);
        Assert.Equal(DateTimeKind.Utc, stored.Kind);
        Assert.Equal(0, stored.Ticks % 10);
        Assert.Equal(new DateTime(2026, 1, 2, 1, 4, 5, DateTimeKind.Utc).AddTicks(1234560), stored);
    }

    [Fact]
    public void Format_UsesFixedMicrosecondUtcLayout()
    {
        var value = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).AddTicks(120);
        Assert.Equal("2026-01-02T03:04:05.000012Z", TimestampPrecision.Format(value));
    }
}
