using RecordAuditTrails.Core.Exceptions;
using RecordAuditTrails.Core.Hashing;

namespace RecordAuditTrails.Core.Reporting;

/// <summary>Renders reports in the requested format and attaches a SHA-256 digest of the output.</summary>
public sealed class ReportExporter(IEnumerable<IAuditReportFormatter> formatters)
{
    private readonly Dictionary<ExportFormat, IAuditReportFormatter> _formatters = formatters.ToDictionary(f => f.Format);

    public static ReportExporter CreateDefault() => new(
    [
        new JsonReportFormatter(),
        new CsvReportFormatter(),
        new TextReportFormatter(),
        new HtmlReportFormatter(),
    ]);

    public static bool TryParseFormat(string? value, out ExportFormat format) =>
        Enum.TryParse(value, ignoreCase: true, out format) && Enum.IsDefined(format);

    public ExportedReport Export(AuditReport report, ExportFormat format)
    {
        if (!_formatters.TryGetValue(format, out var formatter))
        {
            throw new AuditValidationException($"Export format '{format}' is not supported.");
        }
        var content = formatter.Render(report);
        return new ExportedReport(
            content,
            formatter.ContentType,
            $"record-{report.Record.Id:N}-audit-trail.{formatter.FileExtension}",
            CanonicalHasher.Sha256Hex(content));
    }
}
