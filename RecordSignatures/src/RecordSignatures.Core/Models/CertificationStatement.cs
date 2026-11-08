using System.Text;

namespace RecordSignatures.Core.Models;

/// <summary>
/// The 21 CFR 11.100(c) certification: each individual states that their electronic signature is the legally binding
/// equivalent of their handwritten signature. Acceptance is recorded against a statement version.
/// </summary>
public static class CertificationStatement
{
    public const string CurrentVersion = "1.0";

    public static string Statement(string organization) =>
        $"I certify that my electronic signature, applied within the systems of {organization}, is intended to be the " +
        "legally binding equivalent of my traditional handwritten signature. I will not share my identification code or " +
        "password with anyone, and I will report any loss or suspected compromise immediately.";

    /// <summary>A printable certification for one signer, suitable for the organization's regulatory file (11.100(c)).</summary>
    public static string Render(Signer signer, string organization)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CERTIFICATION OF ELECTRONIC SIGNATURE (21 CFR 11.100(c))");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine($"Organization       : {organization}");
        sb.AppendLine($"Printed name       : {signer.PrintedName}");
        sb.AppendLine($"Identification code: {signer.Username}");
        sb.AppendLine($"Employee id        : {signer.EmployeeId}");
        sb.AppendLine($"Signature status   : {signer.Status}");
        sb.AppendLine();
        sb.AppendLine("Identity verification (21 CFR 11.100(b))");
        sb.AppendLine($"  Method           : {signer.Identity.Method}");
        sb.AppendLine($"  Evidence         : {signer.Identity.EvidenceReference}");
        sb.AppendLine($"  Verified by      : {signer.Identity.VerifiedByUsername}");
        sb.AppendLine($"  Verified (UTC)   : {Utc(signer.Identity.VerifiedUtc)}");
        sb.AppendLine();
        sb.AppendLine($"Statement (version {signer.CertificationVersion ?? CurrentVersion})");
        sb.AppendLine($"  {Statement(organization)}");
        sb.AppendLine();
        sb.AppendLine(signer.CertificationAcceptedUtc is { } accepted
            ? $"Accepted (UTC)     : {Utc(accepted)}, after re-entering identification code and password"
            : "NOT YET ACCEPTED by the individual.");
        sb.AppendLine($"Next review due    : {Utc(signer.NextReviewDueUtc)}");
        return sb.ToString();
    }

    private static string Utc(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + " UTC";
}
