using RecordAuditTrails.Core.Hashing;
using RecordAuditTrails.Models;

namespace RecordAuditTrails.Core.Auditing;

/// <summary>Re-computes hash chains to prove that nothing was altered, removed or re-ordered.</summary>
public static class ChainVerifier
{
    public static IntegrityResult VerifyAuditTrail(IEnumerable<AuditTrailEntry> entries)
    {
        var expectedPrevious = CanonicalHasher.GenesisHash;
        long expectedSequence = 1;
        var count = 0;

        foreach (var entry in entries.OrderBy(e => e.Sequence))
        {
            count++;
            if (entry.Sequence != expectedSequence)
            {
                return IntegrityResult.Broken(count, expectedSequence, $"Missing or duplicate entry: expected sequence {expectedSequence}, found {entry.Sequence}.");
            }
            if (!string.Equals(entry.PreviousHash, expectedPrevious, StringComparison.Ordinal))
            {
                return IntegrityResult.Broken(count, entry.Sequence, "Chain link does not match the previous entry.");
            }
            if (!CanonicalHasher.FixedTimeEquals(entry.Hash, AuditHash.ForEntry(entry)))
            {
                return IntegrityResult.Broken(count, entry.Sequence, "Entry content does not match its recorded hash.");
            }
            expectedPrevious = entry.Hash;
            expectedSequence++;
        }
        return IntegrityResult.Valid(count);
    }

    public static IntegrityResult VerifyRevisions(IEnumerable<DocumentRevision> revisions)
    {
        var expectedPrevious = CanonicalHasher.GenesisHash;
        var expectedNumber = 1;
        var count = 0;

        foreach (var revision in revisions.OrderBy(r => r.RevisionNumber))
        {
            count++;
            if (revision.RevisionNumber != expectedNumber)
            {
                return IntegrityResult.Broken(count, expectedNumber, $"Missing or duplicate revision: expected {expectedNumber}, found {revision.RevisionNumber}.");
            }
            if (!string.Equals(revision.PreviousHash, expectedPrevious, StringComparison.Ordinal))
            {
                return IntegrityResult.Broken(count, revision.RevisionNumber, "Chain link does not match the previous revision.");
            }
            if (!CanonicalHasher.FixedTimeEquals(revision.Hash, AuditHash.ForRevision(revision)))
            {
                return IntegrityResult.Broken(count, revision.RevisionNumber, "Revision content does not match its recorded hash.");
            }
            expectedPrevious = revision.Hash;
            expectedNumber++;
        }
        return IntegrityResult.Valid(count);
    }
}
