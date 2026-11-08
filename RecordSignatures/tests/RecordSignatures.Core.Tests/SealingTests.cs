using RecordSignatures.Core.Models;
using RecordSignatures.Core.Sealing;

namespace RecordSignatures.Core.Tests;

public class SealingTests
{
    [Fact]
    public void Hash_DistinguishesFieldBoundaries()
    {
        Assert.NotEqual(CanonicalHasher.Sha256(["ab", "c"]), CanonicalHasher.Sha256(["a", "bc"]));
    }

    [Fact]
    public void Hash_DistinguishesNullFromEmpty()
    {
        Assert.NotEqual(CanonicalHasher.Sha256(["a", null]), CanonicalHasher.Sha256(["a", ""]));
    }

    [Fact]
    public void Hash_IsDeterministicLowercaseHex()
    {
        var hash = CanonicalHasher.Sha256(["x", "y"]);
        Assert.Equal(hash, CanonicalHasher.Sha256(["x", "y"]));
        Assert.Matches("^[0-9a-f]{64}$", hash);
        Assert.Equal(64, CanonicalHasher.Genesis.Length);
    }

    [Fact]
    public void Hmac_DependsOnKeyAndData()
    {
        var k1 = new byte[32];
        var k2 = new byte[32];
        k2[0] = 1;
        Assert.NotEqual(CanonicalHasher.HmacSha256(k1, ["a"]), CanonicalHasher.HmacSha256(k2, ["a"]));
        Assert.NotEqual(CanonicalHasher.HmacSha256(k1, ["a"]), CanonicalHasher.HmacSha256(k1, ["b"]));
    }

    [Fact]
    public void FixedTimeEquals_ComparesStrings()
    {
        Assert.True(CanonicalHasher.FixedTimeEquals("abc", "abc"));
        Assert.False(CanonicalHasher.FixedTimeEquals("abc", "abd"));
        Assert.False(CanonicalHasher.FixedTimeEquals("abc", "abcd"));
    }

    [Fact]
    public void TimestampPrecision_TruncatesToMicrosecondsAndIsUtc()
    {
        var value = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.FromHours(2)).AddTicks(1234567);
        var stored = TimestampPrecision.ToStorage(value);
        Assert.Equal(DateTimeKind.Utc, stored.Kind);
        Assert.Equal(10, stored.Hour);
        Assert.Equal(0, stored.Ticks % 10);
        Assert.Equal("2026-03-01T10:00:00.123456Z", TimestampPrecision.Canonical(stored));
    }

    private static RecordSnapshot Snapshot(int version = 1, string title = "T", Dictionary<string, string?>? fields = null) =>
        new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "BatchRecord", title, fields ?? new() { ["a"] = "1", ["b"] = "2" }, version, false);

    [Fact]
    public void RecordContentHash_IgnoresFieldOrderButNotContent()
    {
        var a = RecordContentHasher.Hash(Snapshot(fields: new() { ["a"] = "1", ["b"] = "2" }));
        var reordered = RecordContentHasher.Hash(Snapshot(fields: new() { ["b"] = "2", ["a"] = "1" }));
        Assert.Equal(a, reordered);
        Assert.NotEqual(a, RecordContentHasher.Hash(Snapshot(fields: new() { ["a"] = "1", ["b"] = "3" })));
        Assert.NotEqual(a, RecordContentHasher.Hash(Snapshot(title: "Other")));
        Assert.NotEqual(a, RecordContentHasher.Hash(Snapshot(version: 2)));
        Assert.NotEqual(a, RecordContentHasher.Hash(Snapshot(fields: new() { ["a"] = "1", ["b"] = null })));
    }

    private static SignatureSealer Sealer(Action<SignatureOptions>? configure = null) => new(TestOptions.Wrap(configure));

    private static SignatureRecord Sample(SignatureSealer sealer)
    {
        var s = new SignatureRecord(Guid.NewGuid(), Guid.NewGuid(), 3, "hash", 1, "u1", "tina", "Tina Tech", "Review", "stmt", "ok",
            new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc), CanonicalHasher.Genesis, sealer.ActiveKeyId, "");
        return s with { SealHash = sealer.Compute(s) };
    }

    [Fact]
    public void Sealer_VerifiesUntouchedSignature()
    {
        var sealer = Sealer();
        Assert.True(sealer.Verify(Sample(sealer)));
    }

    [Fact]
    public void Sealer_DetectsEveryFieldBeingChanged()
    {
        var sealer = Sealer();
        var s = Sample(sealer);
        var tampered = new[]
        {
            s with { Id = Guid.NewGuid() },
            s with { RecordId = Guid.NewGuid() },
            s with { RecordVersion = 4 },
            s with { RecordContentHash = "other" },
            s with { Sequence = 2 },
            s with { SignerUserId = "u2" },
            s with { SignerUsername = "mallory" },
            s with { SignerPrintedName = "Mallory" },
            s with { MeaningCode = "Approval" },
            s with { MeaningText = "x" },
            s with { Comment = "changed" },
            s with { Comment = null },
            s with { SignedUtc = s.SignedUtc.AddSeconds(1) },
            s with { PreviousSealHash = "abc" },
        };
        Assert.All(tampered, t => Assert.False(sealer.Verify(t)));
    }

    [Fact]
    public void Sealer_RejectsUnknownKeyId()
    {
        var sealer = Sealer();
        var s = Sample(sealer);
        Assert.False(sealer.Verify(s with { SealKeyId = "nope" }));
        Assert.Equal("", sealer.Compute(s, "nope"));
    }

    [Fact]
    public void Sealer_OldSignaturesStillVerifyAfterKeyRotation()
    {
        var oldSealer = Sealer();
        var old = Sample(oldSealer);
        var rotated = Sealer(o =>
        {
            o.SealKeys["k2"] = TestOptions.Key2;
            o.ActiveSealKeyId = "k2";
        });
        Assert.Equal("k2", rotated.ActiveKeyId);
        Assert.True(rotated.Verify(old));
        var fresh = Sample(rotated);
        Assert.Equal("k2", fresh.SealKeyId);
        Assert.True(rotated.Verify(fresh));
        Assert.False(oldSealer.Verify(fresh));
    }

    [Fact]
    public void Sealer_DifferentKeysGiveDifferentSeals()
    {
        var a = Sealer();
        var b = Sealer(o => o.SealKeys["k1"] = TestOptions.Key2);
        var s = Sample(a);
        Assert.False(b.Verify(s));
    }

    [Fact]
    public void Sealer_RequiresAtLeastOneKey()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Sealer(o => o.SealKeys.Clear()));
        Assert.Contains("at least one key", ex.Message);
    }

    [Fact]
    public void Sealer_RejectsShortKeys()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Sealer(o => o.SealKeys["k1"] = Convert.ToBase64String(new byte[16])));
        Assert.Contains("at least 32 bytes", ex.Message);
    }

    [Fact]
    public void Sealer_RejectsInvalidBase64()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Sealer(o => o.SealKeys["k1"] = "!!!not base64!!!"));
        Assert.Contains("not valid base64", ex.Message);
    }

    [Fact]
    public void Sealer_RequiresActiveKeyToExist()
    {
        Assert.Throws<InvalidOperationException>(() => Sealer(o => o.ActiveSealKeyId = "missing"));
        Assert.Throws<InvalidOperationException>(() => Sealer(o => o.ActiveSealKeyId = null));
    }
}
