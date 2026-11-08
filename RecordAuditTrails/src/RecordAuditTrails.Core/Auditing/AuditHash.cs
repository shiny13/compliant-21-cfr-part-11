using System.Globalization;
using RecordAuditTrails.Core.Hashing;
using RecordAuditTrails.Models;

namespace RecordAuditTrails.Core.Auditing;

/// <summary>Computes the tamper-evident hash for audit entries and document revisions.</summary>
public static class AuditHash
{
    public static string ForEntry(AuditTrailEntry e) => CanonicalHasher.Hash(
        e.PreviousHash,
        e.RecordId.ToString("D"),
        e.ChangeSetId.ToString("D"),
        e.Sequence.ToString(CultureInfo.InvariantCulture),
        e.RecordVersion.ToString(CultureInfo.InvariantCulture),
        e.Action.ToString(),
        e.FieldName,
        e.OldValue,
        e.NewValue,
        e.Reason,
        e.UserId,
        e.Username,
        e.Role,
        TimestampPrecision.Format(e.TimestampUtc));

    public static string ForRevision(DocumentRevision r) => CanonicalHasher.Hash(
        r.PreviousHash,
        r.DocumentId.ToString("D"),
        r.RevisionNumber.ToString(CultureInfo.InvariantCulture),
        r.Title,
        r.Content,
        r.ChangeDescription,
        r.AuthorId,
        r.Author,
        TimestampPrecision.Format(r.CreatedUtc));
}
