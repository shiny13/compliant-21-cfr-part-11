using System.Text;
using RecordAuditTrails.Core.Hashing;

namespace RecordAuditTrails.Core.Reporting;

/// <summary>Electronic form for spreadsheets: one audit entry per row. Cells that start with a formula character are neutralized.</summary>
public sealed class CsvReportFormatter : IAuditReportFormatter
{
    public ExportFormat Format => ExportFormat.Csv;

    public string ContentType => "text/csv";

    public string FileExtension => "csv";

    public string Render(AuditReport report)
    {
        var sb = new StringBuilder();
        sb.Append("Sequence,Timestamp (UTC),Action,Field,Old Value,New Value,Reason,User,User Id,Role,Record Version,Change Set,Hash\r\n");
        foreach (var e in report.Entries)
        {
            sb.AppendJoin(',',
                Cell(e.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Cell(TimestampPrecision.Format(e.TimestampUtc)),
                Cell(e.Action.ToString()),
                Cell(e.FieldName),
                Cell(e.OldValue),
                Cell(e.NewValue),
                Cell(e.Reason),
                Cell(e.Username),
                Cell(e.UserId),
                Cell(e.Role),
                Cell(e.RecordVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Cell(e.ChangeSetId.ToString("D")),
                Cell(e.Hash));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    internal static string Cell(string? value)
    {
        if (value is null)
        {
            return string.Empty;
        }
        // Prevent spreadsheet formula injection while preserving the recorded text.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
