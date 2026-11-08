namespace RecordSignatures.Core.Models;

/// <summary>
/// An executed electronic signature (11.50, 11.70). Immutable once stored. It carries the printed name, the date and
/// time and the meaning, and is bound to one exact version and content hash of one record by <see cref="SealHash"/>.
/// </summary>
public sealed record SignatureRecord(
    Guid Id,
    Guid RecordId,
    int RecordVersion,
    string RecordContentHash,
    long Sequence,
    string SignerUserId,
    string SignerUsername,
    string SignerPrintedName,
    string MeaningCode,
    string MeaningText,
    string? Comment,
    DateTime SignedUtc,
    string PreviousSealHash,
    string SealKeyId,
    string SealHash);

/// <summary>A signature is withdrawn by adding one of these; the signature row itself is never touched.</summary>
public sealed record SignatureRevocation(
    Guid SignatureId,
    DateTime RevokedUtc,
    string RevokedByUserId,
    string RevokedByUsername,
    string Reason);

/// <summary>Append-only log of every credential check made for a signing or certification.</summary>
public sealed record SigningAttempt(
    Guid Id,
    string UserId,
    string Username,
    DateTime AttemptedUtc,
    AttemptOutcome Outcome,
    Guid? RecordId)
{
    public bool Succeeded => Outcome == AttemptOutcome.Success;
}

/// <summary>A report to the security unit of an attempted or actual unauthorized use of credentials (11.300(d)).</summary>
public sealed record SecurityAlert(
    Guid Id,
    DateTime RaisedUtc,
    AlertSeverity Severity,
    AlertType Type,
    string? UserId,
    string? Username,
    string Detail,
    Guid? AuditRecordId);

/// <summary>The signed record as held by RecordAuditTrails at a point in time.</summary>
public sealed record RecordSnapshot(
    Guid Id,
    string RecordType,
    string Title,
    IReadOnlyDictionary<string, string?> Fields,
    int Version,
    bool IsDeleted);

/// <summary>The outcome of checking one signature.</summary>
public sealed record SignatureVerification(SignatureRecord Signature, SignatureRevocation? Revocation, SignatureStatus Status, string? Detail);

/// <summary>The outcome of checking all signatures of one record.</summary>
public sealed record RecordSignatureVerification(
    RecordSnapshot Record,
    IReadOnlyList<SignatureVerification> Signatures,
    bool IsIntact,
    IReadOnlyList<string> Problems);
