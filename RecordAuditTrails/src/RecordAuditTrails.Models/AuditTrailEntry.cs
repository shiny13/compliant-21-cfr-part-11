namespace RecordAuditTrails.Models;

/// <summary>
/// One immutable, append-only field-level audit entry. Old and new values are both retained, together with
/// who made the change, when (server clock, UTC) and why. Entries form a SHA-256 hash chain per record so any
/// later alteration, removal or reordering is detectable.
/// </summary>
public class AuditTrailEntry
{
    public long Id { get; set; }

    public Guid RecordId { get; set; }

    /// <summary>Groups the field entries produced by a single operator action.</summary>
    public Guid ChangeSetId { get; set; }

    /// <summary>Gap-free, 1-based position of this entry in the record's chain.</summary>
    public long Sequence { get; set; }

    /// <summary>The record version that resulted from this change.</summary>
    public int RecordVersion { get; set; }

    public AuditAction Action { get; set; }

    public string FieldName { get; set; } = string.Empty;

    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    /// <summary>Computer-generated UTC timestamp, truncated to microseconds so it round-trips the database exactly.</summary>
    public DateTime TimestampUtc { get; set; }

    public string PreviousHash { get; set; } = string.Empty;

    public string Hash { get; set; } = string.Empty;
}
