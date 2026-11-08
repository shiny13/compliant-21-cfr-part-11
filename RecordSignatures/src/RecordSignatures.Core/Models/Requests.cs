namespace RecordSignatures.Core.Models;

/// <summary>Who verified the individual and how. The verifier and the time are taken from the token and the server clock.</summary>
public sealed record IdentityVerificationRequest(IdentityVerificationMethod Method, string EvidenceReference);

public sealed record EnrollSignerRequest(
    string UserId,
    string Username,
    string PrintedName,
    string EmployeeId,
    string? JobTitle,
    IdentityVerificationRequest Identity);

/// <summary>The individual re-enters both signature components to accept the certification.</summary>
public sealed record CertifyRequest(string Username, string Password, string StatementVersion);

public sealed record SuspendSignerRequest(string Reason, bool RevokeSessions = true);

public sealed record ReinstateSignerRequest(string Reason, IdentityVerificationRequest Identity);

public sealed record RevokeSignerRequest(string Reason, bool RevokeSessions = true);

public sealed record ReviewSignerRequest(string Reason, IdentityVerificationRequest Identity);

/// <summary>The result of a lifecycle change. <see cref="SessionsRevoked"/> is null when no session revocation was requested.</summary>
public sealed record SignerActionResult(Signer Signer, bool? SessionsRevoked);

/// <summary>
/// A request to sign. <see cref="RecordVersion"/> is the version the signer saw, so a record cannot change between
/// review and signing. <see cref="Username"/> and <see cref="Password"/> are the two signature components, entered
/// again for every signing (11.200(a)).
/// </summary>
public sealed record SignRequest(
    Guid RecordId,
    int RecordVersion,
    string Meaning,
    string? Comment,
    string Username,
    string Password);

public sealed record RevokeSignatureRequest(string Reason);
