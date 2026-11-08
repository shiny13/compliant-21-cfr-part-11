namespace RecordAuditTrails.Models;

/// <summary>
/// The current state of an electronic record. Records are never physically deleted: a delete sets
/// <see cref="IsDeleted"/> and the full history stays in <see cref="AuditTrailEntry"/>.
/// </summary>
public class ElectronicRecord
{
    public Guid Id { get; set; }

    public string RecordType { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Canonical JSON object of string fields (sorted keys).</summary>
    public string DataJson { get; set; } = "{}";

    /// <summary>Incremented on every change; also the optimistic concurrency token.</summary>
    public int Version { get; set; }

    public bool IsDeleted { get; set; }

    /// <summary>Earliest date the record (and its audit trail) may be considered for disposition.</summary>
    public DateTime RetainUntilUtc { get; set; }

    public DateTime CreatedUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public DateTime ModifiedUtc { get; set; }

    public string ModifiedBy { get; set; } = string.Empty;
}
