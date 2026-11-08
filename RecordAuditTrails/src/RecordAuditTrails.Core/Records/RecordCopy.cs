using RecordAuditTrails.Models;

namespace RecordAuditTrails.Core.Records;

/// <summary>Complete, human- and machine-readable snapshot of an electronic record.</summary>
public sealed record RecordCopy(
    Guid Id,
    string RecordType,
    string Title,
    IReadOnlyDictionary<string, string?> Fields,
    int Version,
    bool IsDeleted,
    DateTime RetainUntilUtc,
    DateTime CreatedUtc,
    string CreatedBy,
    DateTime ModifiedUtc,
    string ModifiedBy)
{
    public static RecordCopy From(ElectronicRecord r) => new(
        r.Id, r.RecordType, r.Title, RecordFields.Deserialize(r.DataJson), r.Version, r.IsDeleted,
        r.RetainUntilUtc, r.CreatedUtc, r.CreatedBy, r.ModifiedUtc, r.ModifiedBy);
}
