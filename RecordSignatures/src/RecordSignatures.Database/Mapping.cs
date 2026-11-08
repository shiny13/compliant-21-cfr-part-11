using RecordSignatures.Core.Models;
using RecordSignatures.Database.Entities;

namespace RecordSignatures.Database;

/// <summary>Converts between the Core domain types and the EF entities.</summary>
internal static class Mapping
{
    public static Signer ToDomain(this SignerEntity e) => new()
    {
        Id = e.Id,
        UserId = e.UserId,
        Username = e.Username,
        PrintedName = e.PrintedName,
        EmployeeId = e.EmployeeId,
        JobTitle = e.JobTitle,
        Status = Enum.Parse<SignerStatus>(e.Status),
        StatusReason = e.StatusReason,
        StatusChangedUtc = e.StatusChangedUtc,
        EnrolledUtc = e.EnrolledUtc,
        EnrolledByUserId = e.EnrolledByUserId,
        EnrolledByUsername = e.EnrolledByUsername,
        Identity = new IdentityVerification(
            Enum.Parse<IdentityVerificationMethod>(e.IdentityMethod), e.IdentityEvidence,
            e.IdentityVerifiedByUserId, e.IdentityVerifiedByUsername, e.IdentityVerifiedUtc),
        CertificationVersion = e.CertificationVersion,
        CertificationAcceptedUtc = e.CertificationAcceptedUtc,
        LastReviewedUtc = e.LastReviewedUtc,
        NextReviewDueUtc = e.NextReviewDueUtc,
        Version = e.Version,
    };

    /// <summary>Copies every column except the identity columns (id, user id, username, employee id, enrollment) that never change.</summary>
    public static void CopyMutableTo(this Signer s, SignerEntity e)
    {
        e.PrintedName = s.PrintedName;
        e.JobTitle = s.JobTitle;
        e.Status = s.Status.ToString();
        e.StatusReason = s.StatusReason;
        e.StatusChangedUtc = s.StatusChangedUtc;
        e.IdentityMethod = s.Identity.Method.ToString();
        e.IdentityEvidence = s.Identity.EvidenceReference;
        e.IdentityVerifiedByUserId = s.Identity.VerifiedByUserId;
        e.IdentityVerifiedByUsername = s.Identity.VerifiedByUsername;
        e.IdentityVerifiedUtc = s.Identity.VerifiedUtc;
        e.CertificationVersion = s.CertificationVersion;
        e.CertificationAcceptedUtc = s.CertificationAcceptedUtc;
        e.LastReviewedUtc = s.LastReviewedUtc;
        e.NextReviewDueUtc = s.NextReviewDueUtc;
    }

    public static SignerEntity ToEntity(this Signer s)
    {
        var e = new SignerEntity
        {
            Id = s.Id,
            UserId = s.UserId,
            Username = s.Username,
            EmployeeId = s.EmployeeId,
            EnrolledUtc = s.EnrolledUtc,
            EnrolledByUserId = s.EnrolledByUserId,
            EnrolledByUsername = s.EnrolledByUsername,
            Version = 1,
        };
        s.CopyMutableTo(e);
        return e;
    }

    public static SignatureRecord ToDomain(this SignatureEntity e) => new(
        e.Id, e.RecordId, e.RecordVersion, e.RecordContentHash, e.Sequence, e.SignerUserId, e.SignerUsername,
        e.SignerPrintedName, e.MeaningCode, e.MeaningText, e.Comment, e.SignedUtc, e.PreviousSealHash, e.SealKeyId, e.SealHash);

    public static SignatureEntity ToEntity(this SignatureRecord s) => new()
    {
        Id = s.Id, RecordId = s.RecordId, RecordVersion = s.RecordVersion, RecordContentHash = s.RecordContentHash,
        Sequence = s.Sequence, SignerUserId = s.SignerUserId, SignerUsername = s.SignerUsername,
        SignerPrintedName = s.SignerPrintedName, MeaningCode = s.MeaningCode, MeaningText = s.MeaningText,
        Comment = s.Comment, SignedUtc = s.SignedUtc, PreviousSealHash = s.PreviousSealHash,
        SealKeyId = s.SealKeyId, SealHash = s.SealHash,
    };

    public static SignatureRevocation ToDomain(this SignatureRevocationEntity e) =>
        new(e.SignatureId, e.RevokedUtc, e.RevokedByUserId, e.RevokedByUsername, e.Reason);

    public static SignatureRevocationEntity ToEntity(this SignatureRevocation r) => new()
    {
        Id = Guid.NewGuid(), SignatureId = r.SignatureId, RevokedUtc = r.RevokedUtc,
        RevokedByUserId = r.RevokedByUserId, RevokedByUsername = r.RevokedByUsername, Reason = r.Reason,
    };

    public static SigningAttempt ToDomain(this SigningAttemptEntity e) =>
        new(e.Id, e.UserId, e.Username, e.AttemptedUtc, Enum.Parse<AttemptOutcome>(e.Outcome), e.RecordId);

    public static SigningAttemptEntity ToEntity(this SigningAttempt a) => new()
    {
        Id = a.Id, UserId = a.UserId, Username = a.Username, AttemptedUtc = a.AttemptedUtc,
        Outcome = a.Outcome.ToString(), RecordId = a.RecordId,
    };

    public static SecurityAlert ToDomain(this SecurityAlertEntity e) => new(
        e.Id, e.RaisedUtc, Enum.Parse<AlertSeverity>(e.Severity), Enum.Parse<AlertType>(e.Type),
        e.UserId, e.Username, e.Detail, e.AuditRecordId);

    public static SecurityAlertEntity ToEntity(this SecurityAlert a) => new()
    {
        Id = a.Id, RaisedUtc = a.RaisedUtc, Severity = a.Severity.ToString(), Type = a.Type.ToString(),
        UserId = a.UserId, Username = a.Username, Detail = a.Detail, AuditRecordId = a.AuditRecordId,
    };
}
