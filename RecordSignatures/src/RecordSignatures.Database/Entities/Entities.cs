namespace RecordSignatures.Database.Entities;

/// <summary>Table `signers`. The identity columns are immutable; the rest changes only through audited lifecycle steps.</summary>
public class SignerEntity
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = "";

    public string Username { get; set; } = "";

    public string PrintedName { get; set; } = "";

    public string EmployeeId { get; set; } = "";

    public string? JobTitle { get; set; }

    public string Status { get; set; } = "";

    public string StatusReason { get; set; } = "";

    public DateTime StatusChangedUtc { get; set; }

    public DateTime EnrolledUtc { get; set; }

    public string EnrolledByUserId { get; set; } = "";

    public string EnrolledByUsername { get; set; } = "";

    public string IdentityMethod { get; set; } = "";

    public string IdentityEvidence { get; set; } = "";

    public string IdentityVerifiedByUserId { get; set; } = "";

    public string IdentityVerifiedByUsername { get; set; } = "";

    public DateTime IdentityVerifiedUtc { get; set; }

    public string? CertificationVersion { get; set; }

    public DateTime? CertificationAcceptedUtc { get; set; }

    public DateTime LastReviewedUtc { get; set; }

    public DateTime NextReviewDueUtc { get; set; }

    public int Version { get; set; }
}

/// <summary>Table `signatures`: append-only.</summary>
public class SignatureEntity
{
    public Guid Id { get; set; }

    public Guid RecordId { get; set; }

    public int RecordVersion { get; set; }

    public string RecordContentHash { get; set; } = "";

    public long Sequence { get; set; }

    public string SignerUserId { get; set; } = "";

    public string SignerUsername { get; set; } = "";

    public string SignerPrintedName { get; set; } = "";

    public string MeaningCode { get; set; } = "";

    public string MeaningText { get; set; } = "";

    public string? Comment { get; set; }

    public DateTime SignedUtc { get; set; }

    public string PreviousSealHash { get; set; } = "";

    public string SealKeyId { get; set; } = "";

    public string SealHash { get; set; } = "";
}

/// <summary>Table `signature_revocations`: append-only; at most one per signature.</summary>
public class SignatureRevocationEntity
{
    public Guid Id { get; set; }

    public Guid SignatureId { get; set; }

    public DateTime RevokedUtc { get; set; }

    public string RevokedByUserId { get; set; } = "";

    public string RevokedByUsername { get; set; } = "";

    public string Reason { get; set; } = "";
}

/// <summary>Table `signing_attempts`: append-only log of every credential check.</summary>
public class SigningAttemptEntity
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = "";

    public string Username { get; set; } = "";

    public DateTime AttemptedUtc { get; set; }

    public string Outcome { get; set; } = "";

    public Guid? RecordId { get; set; }
}

/// <summary>Table `security_alerts`: append-only.</summary>
public class SecurityAlertEntity
{
    public Guid Id { get; set; }

    public DateTime RaisedUtc { get; set; }

    public string Severity { get; set; } = "";

    public string Type { get; set; } = "";

    public string? UserId { get; set; }

    public string? Username { get; set; }

    public string Detail { get; set; } = "";

    public Guid? AuditRecordId { get; set; }
}
