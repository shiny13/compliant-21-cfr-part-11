namespace RecordSignatures.Core.Models;

public enum SignerStatus
{
    /// <summary>Enrolled and identity-verified by an administrator; the individual has not yet accepted the 11.100(c) certification.</summary>
    PendingCertification = 0,

    Active = 1,

    /// <summary>Temporarily deauthorized (e.g. suspected loss or compromise). Can be reinstated after re-verification.</summary>
    Suspended = 2,

    /// <summary>Permanently deauthorized. The identity is never reused or reassigned (11.100(a)).</summary>
    Revoked = 3,
}

public enum IdentityVerificationMethod
{
    GovernmentIssuedId = 0,
    EmployeeBadgeInPerson = 1,
    HumanResourcesRecord = 2,
    Other = 3,
}

public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2,
}

public enum AlertType
{
    /// <summary>A signing credential check failed (wrong password).</summary>
    FailedSignatureAuthentication = 0,

    /// <summary>The identification code typed does not belong to the logged-in individual: attempted use of someone else's signature.</summary>
    IdentityMismatch = 1,

    /// <summary>Repeated failures locked the individual's signing ability.</summary>
    SignerLockedOut = 2,

    /// <summary>A signing attempt arrived while the individual was locked out.</summary>
    AttemptWhileLocked = 3,

    /// <summary>An administrator suspended or revoked a signer (lost, stolen or compromised credentials).</summary>
    SignerDeauthorized = 4,

    /// <summary>A signature failed integrity verification.</summary>
    SignatureIntegrityFailure = 5,
}

public enum AttemptOutcome
{
    Success = 0,
    BadCredentials = 1,
    UsernameMismatch = 2,
    SubjectMismatch = 3,
}

public enum SignatureStatus
{
    /// <summary>Seal and chain verify, the signed record is unchanged, and the signature has not been revoked.</summary>
    Valid = 0,

    /// <summary>The signature is intact but the record has been changed since it was signed (the signature covers an earlier version).</summary>
    RecordModifiedSinceSigning = 1,

    /// <summary>The signature was formally withdrawn by a manager.</summary>
    Revoked = 2,

    /// <summary>The stored signature does not match its seal: it was altered after it was made.</summary>
    SealInvalid = 3,

    /// <summary>A signature is missing from, or out of order in, the record's chain.</summary>
    ChainBroken = 4,

    /// <summary>The record has the signed version number but different content than was signed.</summary>
    ContentMismatch = 5,
}
