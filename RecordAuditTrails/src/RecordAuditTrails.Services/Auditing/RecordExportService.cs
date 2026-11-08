using RecordAuditTrails.Core.Auditing;
using RecordAuditTrails.Core.Exceptions;
using RecordAuditTrails.Core.Hashing;
using RecordAuditTrails.Core.Records;
using RecordAuditTrails.Core.Reporting;
using RecordAuditTrails.Database;
using RecordAuditTrails.Models;

namespace RecordAuditTrails.Services.Auditing;

public interface IRecordExportService
{
    /// <summary>
    /// Produces an accurate and complete copy of a record and its audit trail for inspection, review and copying
    /// (11.10(b)), in human-readable (text, HTML) or electronic (JSON, CSV) form. The export itself is logged.
    /// </summary>
    Task<ExportedReport> ExportAsync(Guid recordId, ExportFormat format, Actor actor, CancellationToken ct = default);
}

public sealed class RecordExportService(
    IRecordRepository records,
    IAuditTrailRepository trail,
    IAccessLogRepository accessLog,
    IUnitOfWork unitOfWork,
    ReportExporter exporter,
    TimeProvider clock) : IRecordExportService
{
    public async Task<ExportedReport> ExportAsync(Guid recordId, ExportFormat format, Actor actor, CancellationToken ct = default)
    {
        var record = await records.GetAsync(recordId, ct) ?? throw new NotFoundException($"Record {recordId} was not found.");
        var entries = await trail.GetChainAsync(recordId, ct);
        var now = TimestampPrecision.ToStorage(clock.GetUtcNow());

        var report = new AuditReport(
            RecordCopy.From(record),
            entries,
            ChainVerifier.VerifyAuditTrail(entries),
            now,
            actor.Username);
        var exported = exporter.Export(report, format);

        accessLog.Add(new AccessLogEntry
        {
            SubjectType = AccessSubjects.Record,
            SubjectId = recordId,
            Action = AccessAction.Export,
            UserId = actor.UserId,
            Username = actor.Username,
            Role = actor.Role.ToString(),
            TimestampUtc = now,
            Details = $"{format}; sha256={exported.Sha256}",
        });
        await unitOfWork.SaveChangesAsync(ct);
        return exported;
    }
}
