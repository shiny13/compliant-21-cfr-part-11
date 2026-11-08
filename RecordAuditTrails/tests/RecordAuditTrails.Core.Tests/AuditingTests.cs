using RecordAuditTrails.Core.Auditing;
using RecordAuditTrails.Core.Exceptions;
using RecordAuditTrails.Models;

namespace RecordAuditTrails.Core.Tests;

public class ChangeDetectorTests
{
    [Fact]
    public void Detect_ReportsAddedChangedAndRemovedFieldsInOrder()
    {
        var before = new Dictionary<string, string?> { ["b"] = "1", ["c"] = "old", ["d"] = "gone", ["same"] = "x" };
        var after = new Dictionary<string, string?> { ["a"] = "new", ["b"] = "1", ["c"] = "new", ["same"] = "x" };

        var changes = ChangeDetector.Detect(before, after);

        Assert.Equal(
            [new FieldChange("a", null, "new"), new FieldChange("c", "old", "new"), new FieldChange("d", "gone", null)],
            changes);
    }

    [Fact]
    public void Detect_ReturnsEmptyWhenNothingChanged()
    {
        var state = new Dictionary<string, string?> { ["a"] = "1", ["n"] = null };
        Assert.Empty(ChangeDetector.Detect(state, new Dictionary<string, string?>(state)));
    }
}

public class AuditHashTests
{
    [Fact]
    public void ForEntry_ChangesWhenAnyFieldChanges()
    {
        var original = TestData.Chain(1)[0];
        var baseline = AuditHash.ForEntry(original);

        var mutations = new Action<AuditTrailEntry>[]
        {
            e => e.PreviousHash = "x", e => e.RecordId = Guid.NewGuid(), e => e.ChangeSetId = Guid.NewGuid(),
            e => e.Sequence = 99, e => e.RecordVersion = 99, e => e.Action = AuditAction.Delete,
            e => e.FieldName = "z", e => e.OldValue = "o", e => e.NewValue = "n", e => e.Reason = "different reason",
            e => e.UserId = "u-2", e => e.Username = "mallory", e => e.Role = "Administrator",
            e => e.TimestampUtc = e.TimestampUtc.AddTicks(10),
        };
        foreach (var mutate in mutations)
        {
            var copy = TestData.Chain(1)[0];
            copy.ChangeSetId = original.ChangeSetId;
            mutate(copy);
            Assert.NotEqual(baseline, AuditHash.ForEntry(copy));
        }
    }

    [Fact]
    public void ForRevision_ChangesWhenContentChanges()
    {
        var revision = TestData.Revisions(1)[0];
        var baseline = AuditHash.ForRevision(revision);
        revision.Content += "!";
        Assert.NotEqual(baseline, AuditHash.ForRevision(revision));
    }
}

public class ChainVerifierTests
{
    [Fact]
    public void VerifyAuditTrail_AcceptsIntactChain()
    {
        var result = ChainVerifier.VerifyAuditTrail(TestData.Chain(5));
        Assert.True(result.IsValid);
        Assert.Equal(5, result.ItemsChecked);
        Assert.Null(result.FirstBrokenPosition);
    }

    [Fact]
    public void VerifyAuditTrail_AcceptsEmptyTrail() => Assert.True(ChainVerifier.VerifyAuditTrail([]).IsValid);

    [Fact]
    public void VerifyAuditTrail_AcceptsUnorderedInput()
    {
        var chain = TestData.Chain(4);
        chain.Reverse();
        Assert.True(ChainVerifier.VerifyAuditTrail(chain).IsValid);
    }

    [Fact]
    public void VerifyAuditTrail_DetectsAlteredValue()
    {
        var chain = TestData.Chain(4);
        chain[2].NewValue = "tampered";
        var result = ChainVerifier.VerifyAuditTrail(chain);
        Assert.False(result.IsValid);
        Assert.Equal(3, result.FirstBrokenPosition);
        Assert.Contains("recorded hash", result.Problem);
    }

    [Fact]
    public void VerifyAuditTrail_DetectsRemovedEntry()
    {
        var chain = TestData.Chain(4);
        chain.RemoveAt(1);
        var result = ChainVerifier.VerifyAuditTrail(chain);
        Assert.False(result.IsValid);
        Assert.Equal(2, result.FirstBrokenPosition);
        Assert.Contains("Missing", result.Problem);
    }

    [Fact]
    public void VerifyAuditTrail_DetectsRemovedFirstEntry()
    {
        var chain = TestData.Chain(3);
        chain.RemoveAt(0);
        Assert.False(ChainVerifier.VerifyAuditTrail(chain).IsValid);
    }

    [Fact]
    public void VerifyAuditTrail_DetectsBrokenLink()
    {
        var chain = TestData.Chain(3);
        chain[1].PreviousHash = new string('a', 64);
        chain[1].Hash = AuditHash.ForEntry(chain[1]); // attacker re-hashes the altered entry...
        var result = ChainVerifier.VerifyAuditTrail(chain);
        Assert.False(result.IsValid); // ...but the link to the previous entry no longer matches
        Assert.Equal(2, result.FirstBrokenPosition);
        Assert.Contains("link", result.Problem);
    }

    [Fact]
    public void VerifyRevisions_AcceptsIntactChain() => Assert.True(ChainVerifier.VerifyRevisions(TestData.Revisions(3)).IsValid);

    [Fact]
    public void VerifyRevisions_DetectsAlteredContent()
    {
        var revisions = TestData.Revisions(3);
        revisions[0].Content = "secretly edited";
        var result = ChainVerifier.VerifyRevisions(revisions);
        Assert.False(result.IsValid);
        Assert.Equal(1, result.FirstBrokenPosition);
    }

    [Fact]
    public void VerifyRevisions_DetectsMissingRevision()
    {
        var revisions = TestData.Revisions(3);
        revisions.RemoveAt(1);
        var result = ChainVerifier.VerifyRevisions(revisions);
        Assert.False(result.IsValid);
        Assert.Contains("Missing", result.Problem);
    }

    [Fact]
    public void VerifyRevisions_DetectsBrokenLink()
    {
        var revisions = TestData.Revisions(2);
        revisions[1].PreviousHash = "bogus";
        revisions[1].Hash = AuditHash.ForRevision(revisions[1]);
        Assert.Contains("link", ChainVerifier.VerifyRevisions(revisions).Problem);
    }

    [Fact]
    public void IntegrityResult_FactoriesSetFlags()
    {
        Assert.True(IntegrityResult.Valid(2).IsValid);
        var broken = IntegrityResult.Broken(2, 7, "x");
        Assert.False(broken.IsValid);
        Assert.Equal(7, broken.FirstBrokenPosition);
    }
}

public class ReasonValidatorTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abcd")]
    public void Require_RejectsMissingOrTooShortReason(string? reason) =>
        Assert.Throws<AuditValidationException>(() => ReasonValidator.Require(reason));

    [Fact]
    public void Require_RejectsTooLongReason() =>
        Assert.Throws<AuditValidationException>(() => ReasonValidator.Require(new string('x', ReasonValidator.MaximumLength + 1)));

    [Fact]
    public void Require_ReturnsTrimmedReason() => Assert.Equal("fixed typo", ReasonValidator.Require("  fixed typo \n"));
}

public class ExceptionTests
{
    [Fact]
    public void AllDomainExceptionsDeriveFromBaseAndKeepMessage()
    {
        AuditTrailException[] all =
        [
            new AuditValidationException("a"), new NotFoundException("a"), new ConcurrencyConflictException("a"),
            new AccessDeniedException("a"), new ImmutableDataException("a"),
        ];
        Assert.All(all, e => Assert.Equal("a", e.Message));
    }
}
