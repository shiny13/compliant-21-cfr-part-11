using System.Globalization;
using RecordSignatures.Core.Models;
using RecordSignatures.Core.Sealing;

namespace RecordSignatures.Core.Services;

/// <summary>
/// Builds the full field set written to RecordAuditTrails for each entity. RecordAuditTrails keeps the old value, new
/// value, user, time and reason of every field change, so each lifecycle step is recorded there automatically.
/// </summary>
public static class AuditFields
{
    public const string SignerRecordType = "SignerEnrollment";
    public const string SignatureRecordType = "ElectronicSignature";
    public const string AlertRecordType = "SecurityAlert";

    public static Dictionary<string, string?> ForSigner(Signer s) => new(StringComparer.Ordinal)
    {
        ["userId"] = s.UserId,
        ["username"] = s.Username,
        ["printedName"] = s.PrintedName,
        ["employeeId"] = s.EmployeeId,
        ["jobTitle"] = s.JobTitle,
        ["status"] = s.Status.ToString(),
        ["statusReason"] = s.StatusReason,
        ["statusChangedUtc"] = TimestampPrecision.Canonical(s.StatusChangedUtc),
        ["enrolledBy"] = s.EnrolledByUsername,
        ["identityMethod"] = s.Identity.Method.ToString(),
        ["identityEvidence"] = s.Identity.EvidenceReference,
        ["identityVerifiedBy"] = s.Identity.VerifiedByUsername,
        ["identityVerifiedUtc"] = TimestampPrecision.Canonical(s.Identity.VerifiedUtc),
        ["certificationVersion"] = s.CertificationVersion,
        ["certificationAcceptedUtc"] = s.CertificationAcceptedUtc is { } c ? TimestampPrecision.Canonical(c) : null,
        ["lastReviewedUtc"] = TimestampPrecision.Canonical(s.LastReviewedUtc),
        ["nextReviewDueUtc"] = TimestampPrecision.Canonical(s.NextReviewDueUtc),
    };

    /// <summary>The seal is not included: the signature's id is only known once this record exists.</summary>
    public static Dictionary<string, string?> ForNewSignature(
        Guid recordId, int recordVersion, string contentHash, Signer signer, SignatureMeaning meaning, string? comment, DateTime signedUtc) =>
        Signature("Executed", recordId, recordVersion, contentHash, signer.UserId, signer.Username, signer.PrintedName, meaning.Code, meaning.Statement, comment, signedUtc);

    public static Dictionary<string, string?> ForRevokedSignature(SignatureRecord s, SignatureRevocation r)
    {
        var fields = Signature("Revoked", s.RecordId, s.RecordVersion, s.RecordContentHash, s.SignerUserId, s.SignerUsername,
            s.SignerPrintedName, s.MeaningCode, s.MeaningText, s.Comment, s.SignedUtc);
        fields["sealHash"] = s.SealHash;
        fields["revokedBy"] = r.RevokedByUsername;
        fields["revokedUtc"] = TimestampPrecision.Canonical(r.RevokedUtc);
        fields["revocationReason"] = r.Reason;
        return fields;
    }

    private static Dictionary<string, string?> Signature(
        string status, Guid recordId, int recordVersion, string contentHash, string userId, string username, string printedName,
        string meaningCode, string meaningStatement, string? comment, DateTime signedUtc) => new(StringComparer.Ordinal)
    {
        ["status"] = status,
        ["signedRecordId"] = recordId.ToString("D"),
        ["signedRecordVersion"] = recordVersion.ToString(CultureInfo.InvariantCulture),
        ["signedContentHash"] = contentHash,
        ["signerUserId"] = userId,
        ["signerUsername"] = username,
        ["signerPrintedName"] = printedName,
        ["meaning"] = meaningCode,
        ["meaningStatement"] = meaningStatement,
        ["comment"] = comment,
        ["signedUtc"] = TimestampPrecision.Canonical(signedUtc),
    };

    public static Dictionary<string, string?> ForAlert(SecurityAlert a) => new(StringComparer.Ordinal)
    {
        ["severity"] = a.Severity.ToString(),
        ["type"] = a.Type.ToString(),
        ["userId"] = a.UserId,
        ["username"] = a.Username,
        ["detail"] = a.Detail,
        ["raisedUtc"] = TimestampPrecision.Canonical(a.RaisedUtc),
    };
}
