using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RecordSignatures.Core.Models;
using RecordSignatures.Core.Sealing;

namespace RecordSignatures.Core.Reporting;

public enum ReportFormat
{
    Json,
    Text,
    Html,
}

public sealed record ExportedReport(string Content, string ContentType, string Sha256);

/// <summary>
/// The human-readable and electronic form of a signed record's signatures (11.50(b)): for every signature the printed name,
/// the date and time of signing and the meaning, with its current verification status.
/// </summary>
public static class SignatureReportExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static bool TryParseFormat(string? value, out ReportFormat format) =>
        Enum.TryParse(value, ignoreCase: true, out format) && Enum.IsDefined(format);

    public static ExportedReport Export(RecordSignatureVerification verification, DateTime generatedUtc, string generatedBy, ReportFormat format)
    {
        var (content, type) = format switch
        {
            ReportFormat.Json => (ToJson(verification, generatedUtc, generatedBy), "application/json"),
            ReportFormat.Text => (ToText(verification, generatedUtc, generatedBy), "text/plain"),
            _ => (ToHtml(verification, generatedUtc, generatedBy), "text/html"),
        };
        return new ExportedReport(content, type, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))));
    }

    private static string ToJson(RecordSignatureVerification v, DateTime generatedUtc, string generatedBy) =>
        JsonSerializer.Serialize(new
        {
            generatedUtc = TimestampPrecision.Canonical(generatedUtc),
            generatedBy,
            record = new { v.Record.Id, v.Record.RecordType, v.Record.Title, v.Record.Version, v.Record.IsDeleted },
            v.IsIntact,
            v.Problems,
            signatures = v.Signatures.Select(s => new
            {
                s.Signature.Id,
                s.Signature.Sequence,
                printedName = s.Signature.SignerPrintedName,
                username = s.Signature.SignerUsername,
                signedUtc = TimestampPrecision.Canonical(s.Signature.SignedUtc),
                meaning = s.Signature.MeaningCode,
                meaningStatement = s.Signature.MeaningText,
                s.Signature.Comment,
                signedRecordVersion = s.Signature.RecordVersion,
                signedContentHash = s.Signature.RecordContentHash,
                s.Signature.SealHash,
                status = s.Status,
                s.Detail,
                revokedBy = s.Revocation?.RevokedByUsername,
                revokedUtc = s.Revocation is null ? null : TimestampPrecision.Canonical(s.Revocation.RevokedUtc),
            }),
        }, JsonOptions);

    private static string ToText(RecordSignatureVerification v, DateTime generatedUtc, string generatedBy)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ELECTRONIC SIGNATURE REPORT (21 CFR 11.50 / 11.70)");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine($"Record        : {v.Record.Title}");
        sb.AppendLine($"Record id     : {v.Record.Id}");
        sb.AppendLine($"Record type   : {v.Record.RecordType}");
        sb.AppendLine($"Version now   : {v.Record.Version}{(v.Record.IsDeleted ? " (deleted)" : "")}");
        sb.AppendLine($"Generated     : {TimestampPrecision.Canonical(generatedUtc)} by {generatedBy}");
        sb.AppendLine($"Integrity     : {(v.IsIntact ? "all signatures intact" : "PROBLEMS FOUND")}");
        foreach (var problem in v.Problems)
        {
            sb.AppendLine($"  ! {problem}");
        }
        sb.AppendLine();
        if (v.Signatures.Count == 0)
        {
            sb.AppendLine("This record has not been signed.");
        }
        foreach (var s in v.Signatures)
        {
            var sig = s.Signature;
            sb.AppendLine($"Signature #{sig.Sequence}  [{s.Status}]");
            sb.AppendLine($"  Signed by    : {sig.SignerPrintedName} ({sig.SignerUsername})");
            sb.AppendLine($"  Date/time    : {TimestampPrecision.Canonical(sig.SignedUtc)} (UTC)");
            sb.AppendLine($"  Meaning      : {sig.MeaningCode} - {sig.MeaningText}");
            sb.AppendLine($"  Record ver.  : {sig.RecordVersion}");
            if (sig.Comment is not null)
            {
                sb.AppendLine($"  Comment      : {sig.Comment}");
            }
            if (s.Detail is not null)
            {
                sb.AppendLine($"  Note         : {s.Detail}");
            }
            sb.AppendLine($"  Signature id : {sig.Id}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string ToHtml(RecordSignatureVerification v, DateTime generatedUtc, string generatedBy)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Electronic signature report</title>");
        sb.AppendLine("<style>body{font-family:sans-serif;margin:2em}table{border-collapse:collapse;width:100%;margin-top:1em}th,td{border:1px solid #888;padding:.4em;text-align:left;vertical-align:top}.bad{color:#a00;font-weight:bold}</style></head><body>");
        sb.AppendLine("<h1>Electronic signature report</h1>");
        sb.AppendLine($"<p><strong>Record:</strong> {E(v.Record.Title)} ({E(v.Record.RecordType)})<br><strong>Record id:</strong> {v.Record.Id}<br>");
        sb.AppendLine($"<strong>Current version:</strong> {v.Record.Version}{(v.Record.IsDeleted ? " (deleted)" : "")}<br>");
        sb.AppendLine($"<strong>Generated:</strong> {TimestampPrecision.Canonical(generatedUtc)} by {E(generatedBy)}</p>");
        sb.AppendLine(v.IsIntact
            ? "<p>All signatures are intact.</p>"
            : $"<p class=\"bad\">Problems found: {E(string.Join("; ", v.Problems))}</p>");
        if (v.Signatures.Count == 0)
        {
            sb.AppendLine("<p>This record has not been signed.</p>");
        }
        else
        {
            sb.AppendLine("<table><tr><th>#</th><th>Printed name</th><th>Date/time (UTC)</th><th>Meaning</th><th>Version</th><th>Status</th><th>Comment</th></tr>");
            foreach (var s in v.Signatures)
            {
                var sig = s.Signature;
                sb.AppendLine($"<tr><td>{sig.Sequence}</td><td>{E(sig.SignerPrintedName)} ({E(sig.SignerUsername)})</td>" +
                    $"<td>{TimestampPrecision.Canonical(sig.SignedUtc)}</td><td>{E(sig.MeaningCode)}: {E(sig.MeaningText)}</td>" +
                    $"<td>{sig.RecordVersion}</td><td>{s.Status}{(s.Detail is null ? "" : "<br>" + E(s.Detail))}</td><td>{E(sig.Comment)}</td></tr>");
            }
            sb.AppendLine("</table>");
        }
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}
