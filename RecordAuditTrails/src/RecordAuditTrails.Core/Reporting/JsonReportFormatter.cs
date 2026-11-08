using System.Text.Json;
using System.Text.Json.Serialization;
using RecordAuditTrails.Core.Hashing;

namespace RecordAuditTrails.Core.Reporting;

/// <summary>Electronic form suitable for inspection, review and machine processing.</summary>
public sealed class JsonReportFormatter : IAuditReportFormatter
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public ExportFormat Format => ExportFormat.Json;

    public string ContentType => "application/json";

    public string FileExtension => "json";

    public string Render(AuditReport report) => JsonSerializer.Serialize(new
    {
        generatedUtc = TimestampPrecision.Format(report.GeneratedUtc),
        report.GeneratedBy,
        record = report.Record,
        integrity = report.Integrity,
        auditTrail = report.Entries,
    }, Options);
}
