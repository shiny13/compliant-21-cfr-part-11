using RecordSignatures.Core.Models;
using RecordSignatures.Core.Reporting;
using RecordSignatures.Core.Sealing;

namespace RecordSignatures.Core.Services;

public interface ISignatureReportService
{
    Task<ExportedReport> ExportAsync(Guid recordId, ReportFormat format, Operator caller, CancellationToken ct = default);
}

public sealed class SignatureReportService(ISignatureService signatures, TimeProvider clock) : ISignatureReportService
{
    public async Task<ExportedReport> ExportAsync(Guid recordId, ReportFormat format, Operator caller, CancellationToken ct = default)
    {
        var verification = await signatures.VerifyRecordAsync(recordId, caller, ct);
        return SignatureReportExporter.Export(verification, TimestampPrecision.ToStorage(clock.GetUtcNow()), caller.Username, format);
    }
}
