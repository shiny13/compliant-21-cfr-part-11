using System.Net;
using System.Text;
using RecordAuditTrails.Core.Hashing;

namespace RecordAuditTrails.Core.Reporting;

/// <summary>Printable, human-readable copy. All recorded values are HTML-encoded.</summary>
public sealed class HtmlReportFormatter : IAuditReportFormatter
{
    public ExportFormat Format => ExportFormat.Html;

    public string ContentType => "text/html";

    public string FileExtension => "html";

    private static string E(string? v) => WebUtility.HtmlEncode(v ?? string.Empty);

    public string Render(AuditReport report)
    {
        var r = report.Record;
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Audit trail report</title>")
          .Append("<style>body{font-family:sans-serif}table{border-collapse:collapse}td,th{border:1px solid #888;padding:4px 8px;text-align:left}")
          .Append(".bad{color:#b00020;font-weight:bold}</style></head><body>");
        sb.Append("<h1>Electronic record and audit trail report</h1>");
        sb.Append($"<p>Generated (UTC) {E(TimestampPrecision.Format(report.GeneratedUtc))} by {E(report.GeneratedBy)}</p>");
        sb.Append("<h2>Record</h2><table>");
        Row(sb, "Id", r.Id.ToString());
        Row(sb, "Type", r.RecordType);
        Row(sb, "Title", r.Title);
        Row(sb, "Version", r.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Row(sb, "Deleted", r.IsDeleted ? "Yes" : "No");
        Row(sb, "Created", $"{TimestampPrecision.Format(r.CreatedUtc)} by {r.CreatedBy}");
        Row(sb, "Last modified", $"{TimestampPrecision.Format(r.ModifiedUtc)} by {r.ModifiedBy}");
        Row(sb, "Retain until", TimestampPrecision.Format(r.RetainUntilUtc));
        foreach (var (name, value) in r.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            Row(sb, name, value ?? "(none)");
        }
        sb.Append("</table><h2>Integrity</h2>");
        sb.Append(report.Integrity.IsValid
            ? $"<p>Audit trail verified: {report.Integrity.ItemsChecked} entries, hash chain intact.</p>"
            : $"<p class=\"bad\">Integrity failure at position {report.Integrity.FirstBrokenPosition}: {E(report.Integrity.Problem)}</p>");
        sb.Append("<h2>Audit trail</h2><table><tr><th>#</th><th>Time (UTC)</th><th>Action</th><th>Field</th><th>Old</th><th>New</th><th>Reason</th><th>User</th><th>Role</th></tr>");
        foreach (var e in report.Entries)
        {
            sb.Append("<tr>")
              .Append($"<td>{e.Sequence}</td><td>{E(TimestampPrecision.Format(e.TimestampUtc))}</td><td>{e.Action}</td>")
              .Append($"<td>{E(e.FieldName)}</td><td>{E(e.OldValue)}</td><td>{E(e.NewValue)}</td>")
              .Append($"<td>{E(e.Reason)}</td><td>{E(e.Username)}</td><td>{E(e.Role)}</td></tr>");
        }
        sb.Append("</table></body></html>");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string name, string value) =>
        sb.Append($"<tr><th>{E(name)}</th><td>{E(value)}</td></tr>");
}
