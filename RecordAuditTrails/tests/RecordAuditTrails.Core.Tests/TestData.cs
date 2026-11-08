using RecordAuditTrails.Core.Auditing;
using RecordAuditTrails.Core.Hashing;
using RecordAuditTrails.Core.Records;
using RecordAuditTrails.Core.Reporting;
using RecordAuditTrails.Models;

namespace RecordAuditTrails.Core.Tests;

internal static class TestData
{
    public static readonly Guid RecordId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly DateTime T0 = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    public static List<AuditTrailEntry> Chain(int count)
    {
        var list = new List<AuditTrailEntry>();
        var previous = CanonicalHasher.GenesisHash;
        for (var i = 1; i <= count; i++)
        {
            var e = new AuditTrailEntry
            {
                RecordId = RecordId,
                ChangeSetId = Guid.NewGuid(),
                Sequence = i,
                RecordVersion = 1,
                Action = i == 1 ? AuditAction.Create : AuditAction.Modify,
                FieldName = $"Field{i}",
                OldValue = i == 1 ? null : $"old{i}",
                NewValue = $"new{i}",
                Reason = "Because of testing",
                UserId = "u-1",
                Username = "alice",
                Role = "LabTechnician",
                TimestampUtc = T0.AddSeconds(i),
                PreviousHash = previous,
            };
            e.Hash = AuditHash.ForEntry(e);
            previous = e.Hash;
            list.Add(e);
        }
        return list;
    }

    public static List<DocumentRevision> Revisions(int count)
    {
        var list = new List<DocumentRevision>();
        var previous = CanonicalHasher.GenesisHash;
        var docId = Guid.NewGuid();
        for (var i = 1; i <= count; i++)
        {
            var r = new DocumentRevision
            {
                DocumentId = docId,
                RevisionNumber = i,
                Title = "SOP",
                Content = $"content {i}",
                ChangeDescription = "Revised for testing",
                AuthorId = "u-9",
                Author = "admin",
                CreatedUtc = T0.AddMinutes(i),
                PreviousHash = previous,
            };
            r.Hash = AuditHash.ForRevision(r);
            previous = r.Hash;
            list.Add(r);
        }
        return list;
    }

    public static RecordCopy Copy(bool deleted = false) => new(
        RecordId, "BatchRecord", "Batch <1>", new Dictionary<string, string?> { ["pH"] = "7.2", ["Note"] = null, ["Alpha"] = "a,\"b\"" },
        3, deleted, T0.AddYears(7), T0, "alice", T0.AddDays(1), "bob");

    public static AuditReport Report(bool valid = true, bool deleted = false)
    {
        var entries = Chain(3);
        entries[1].NewValue = "=SUM(A1)";
        entries[2].OldValue = "<script>alert(1)</script>";
        return new AuditReport(
            Copy(deleted),
            entries,
            valid ? IntegrityResult.Valid(3) : IntegrityResult.Broken(3, 2, "Entry content does not match its recorded hash."),
            T0.AddDays(2),
            "reviewer");
    }
}
