using RecordSignatures.Core.Exceptions;

namespace RecordSignatures.Core.Models;

public sealed record IdentityVerification(
    IdentityVerificationMethod Method,
    string EvidenceReference,
    string VerifiedByUserId,
    string VerifiedByUsername,
    DateTime VerifiedUtc);

/// <summary>
/// An individual sanctioned to apply electronic signatures (11.100). The identity (<see cref="UserId"/>,
/// <see cref="Username"/>) never changes and is never reassigned. Status changes are driven by the methods below,
/// which enforce the legal state transitions.
/// </summary>
public sealed class Signer
{
    /// <summary>Equals the id of this signer's enrollment record in RecordAuditTrails.</summary>
    public Guid Id { get; set; }

    /// <summary>The AuthenticationCore subject (`sub`). Unique to one individual and never reassigned.</summary>
    public string UserId { get; set; } = "";

    /// <summary>Lower-cased login name (the signature's identification code).</summary>
    public string Username { get; set; } = "";

    public string PrintedName { get; set; } = "";

    /// <summary>Organization-assigned identifier of the person (e.g. employee number); one live enrollment per person.</summary>
    public string EmployeeId { get; set; } = "";

    public string? JobTitle { get; set; }

    public SignerStatus Status { get; set; }

    public string StatusReason { get; set; } = "";

    public DateTime StatusChangedUtc { get; set; }

    public DateTime EnrolledUtc { get; set; }

    public string EnrolledByUserId { get; set; } = "";

    public string EnrolledByUsername { get; set; } = "";

    public IdentityVerification Identity { get; set; } = null!;

    public string? CertificationVersion { get; set; }

    public DateTime? CertificationAcceptedUtc { get; set; }

    public DateTime LastReviewedUtc { get; set; }

    public DateTime NextReviewDueUtc { get; set; }

    /// <summary>Optimistic concurrency token, incremented by the store on every update.</summary>
    public int Version { get; set; }

    public bool IsReviewOverdue(DateTime nowUtc) => nowUtc >= NextReviewDueUtc;

    /// <summary>Throws unless every precondition for applying a signature holds.</summary>
    public void EnsureCanSign(DateTime nowUtc)
    {
        switch (Status)
        {
            case SignerStatus.PendingCertification:
                throw new ForbiddenException("The signer has not yet accepted the electronic signature certification (21 CFR 11.100(c)).");
            case SignerStatus.Suspended:
                throw new ForbiddenException("The electronic signature is suspended.");
            case SignerStatus.Revoked:
                throw new ForbiddenException("The electronic signature has been revoked.");
        }
        if (IsReviewOverdue(nowUtc))
        {
            throw new ForbiddenException("The periodic review of this electronic signature is overdue; an administrator must re-verify the signer.");
        }
    }

    public void AcceptCertification(string statementVersion, DateTime nowUtc)
    {
        if (Status != SignerStatus.PendingCertification)
        {
            throw new ConflictException(Status == SignerStatus.Active
                ? "The certification has already been accepted."
                : $"A {Status} signer cannot accept the certification.");
        }
        CertificationVersion = statementVersion;
        CertificationAcceptedUtc = nowUtc;
        SetStatus(SignerStatus.Active, "Certification accepted", nowUtc);
    }

    public void Suspend(string reason, DateTime nowUtc)
    {
        if (Status is not (SignerStatus.PendingCertification or SignerStatus.Active))
        {
            throw new ConflictException($"Only a pending or active signer can be suspended; this signer is {Status}.");
        }
        SetStatus(SignerStatus.Suspended, reason, nowUtc);
    }

    public void Reinstate(string reason, IdentityVerification identity, DateTime nowUtc, int reviewIntervalMonths)
    {
        if (Status != SignerStatus.Suspended)
        {
            throw new ConflictException($"Only a suspended signer can be reinstated; this signer is {Status}.");
        }
        Identity = identity;
        ScheduleReview(nowUtc, reviewIntervalMonths);
        SetStatus(CertificationAcceptedUtc is null ? SignerStatus.PendingCertification : SignerStatus.Active, reason, nowUtc);
    }

    public void Revoke(string reason, DateTime nowUtc)
    {
        if (Status == SignerStatus.Revoked)
        {
            throw new ConflictException("The signer is already revoked.");
        }
        SetStatus(SignerStatus.Revoked, reason, nowUtc);
    }

    /// <summary>Periodic re-verification of the individual (11.300(b)).</summary>
    public void Review(string reason, IdentityVerification identity, DateTime nowUtc, int reviewIntervalMonths)
    {
        if (Status != SignerStatus.Active)
        {
            throw new ConflictException($"Only an active signer can be reviewed; this signer is {Status}.");
        }
        Identity = identity;
        ScheduleReview(nowUtc, reviewIntervalMonths);
        StatusReason = reason;
        StatusChangedUtc = nowUtc;
    }

    public void ScheduleReview(DateTime nowUtc, int intervalMonths)
    {
        LastReviewedUtc = nowUtc;
        NextReviewDueUtc = nowUtc.AddMonths(intervalMonths);
    }

    private void SetStatus(SignerStatus status, string reason, DateTime nowUtc)
    {
        Status = status;
        StatusReason = reason;
        StatusChangedUtc = nowUtc;
    }
}
