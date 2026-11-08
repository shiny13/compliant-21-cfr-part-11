using RecordAuditTrails.Core.Auditing;
using RecordAuditTrails.Core.Records;
using RecordAuditTrails.Models;

namespace RecordAuditTrails.Core.Reporting;

public enum ExportFormat
{
    Json,
    Csv,
    Text,
    Html,
}

/// <summary>Everything an inspector needs: the record, its full audit trail and proof that the trail is intact.</summary>
public sealed record AuditReport(
    RecordCopy Record,
    IReadOnlyList<AuditTrailEntry> Entries,
    IntegrityResult Integrity,
    DateTime GeneratedUtc,
    string GeneratedBy);

/// <summary>The rendered copy plus a SHA-256 digest so the recipient can confirm it was not altered after export.</summary>
public sealed record ExportedReport(string Content, string ContentType, string FileName, string Sha256);

public interface IAuditReportFormatter
{
    ExportFormat Format { get; }

    string ContentType { get; }

    string FileExtension { get; }

    string Render(AuditReport report);
}
