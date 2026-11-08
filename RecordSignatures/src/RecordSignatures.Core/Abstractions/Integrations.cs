using RecordSignatures.Core.Models;

namespace RecordSignatures.Core.Abstractions;

/// <summary>The result of checking an identification code and password with the identity provider.</summary>
public sealed record CredentialResult(bool Verified, string? Subject)
{
    public static CredentialResult Rejected { get; } = new(false, null);

    public static CredentialResult Accepted(string subject) => new(true, subject);
}

/// <summary>Verifies an identification code + password pair (the two components of an electronic signature, 11.200(a)).</summary>
public interface ICredentialVerifier
{
    /// <exception cref="Exceptions.DependencyUnavailableException">The identity provider could not be reached.</exception>
    Task<CredentialResult> VerifyAsync(string username, string password, CancellationToken ct = default);
}

/// <summary>Cuts off a deauthorized individual's sessions in the identity provider (11.300(c)).</summary>
public interface ISessionRevoker
{
    /// <returns>True when the identity provider confirmed the revocation.</returns>
    Task<bool> RevokeUserSessionsAsync(string userId, string administratorAccessToken, CancellationToken ct = default);
}

/// <summary>The RecordAuditTrails API. Calls carry the caller's token, so the audit trail names the same individual.</summary>
public interface IAuditTrailClient
{
    /// <exception cref="Exceptions.NotFoundException">The record does not exist.</exception>
    Task<RecordSnapshot> GetRecordAsync(Guid recordId, string accessToken, CancellationToken ct = default);

    /// <returns>The id of the new record, which becomes the id of the entity it documents.</returns>
    Task<Guid> CreateRecordAsync(string recordType, string title, IReadOnlyDictionary<string, string?> fields, string reason, string accessToken, CancellationToken ct = default);

    Task UpdateRecordAsync(Guid recordId, IReadOnlyDictionary<string, string?> fields, string reason, string accessToken, CancellationToken ct = default);
}

/// <summary>Receives every security alert. Failures in one sink never block the others or the operation that raised the alert.</summary>
public interface ISecurityAlertSink
{
    Task PublishAsync(SecurityAlert alert, Operator? caller, CancellationToken ct = default);
}
