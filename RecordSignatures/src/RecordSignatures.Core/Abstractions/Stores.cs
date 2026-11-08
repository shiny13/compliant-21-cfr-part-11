using RecordSignatures.Core.Models;

namespace RecordSignatures.Core.Abstractions;

/// <summary>Everything the core needs from persistence. Implemented by the Database project.</summary>
public interface ISignerStore
{
    Task<Signer?> FindByUserIdAsync(string userId, CancellationToken ct = default);

    Task<Signer?> FindByUsernameAsync(string username, CancellationToken ct = default);

    /// <summary>True when a non-revoked enrollment already uses this employee id.</summary>
    Task<bool> EmployeeIdInUseAsync(string employeeId, CancellationToken ct = default);

    Task<IReadOnlyList<Signer>> ListAsync(SignerStatus? status, bool reviewDueOnly, DateTime nowUtc, int skip, int take, CancellationToken ct = default);

    /// <exception cref="Exceptions.ConflictException">The user id, username or employee id is already enrolled.</exception>
    Task AddAsync(Signer signer, CancellationToken ct = default);

    /// <summary>Persists changes if <see cref="Signer.Version"/> still matches, then increments it.</summary>
    /// <exception cref="Exceptions.ConflictException">Someone else changed the signer first.</exception>
    Task UpdateAsync(Signer signer, CancellationToken ct = default);
}

public interface ISignatureStore
{
    /// <exception cref="Exceptions.ConflictException">Another signature took the same position in the record's chain.</exception>
    Task AddAsync(SignatureRecord signature, CancellationToken ct = default);

    Task<SignatureRecord?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>All signatures of a record in chain order.</summary>
    Task<IReadOnlyList<SignatureRecord>> ListForRecordAsync(Guid recordId, CancellationToken ct = default);

    Task<SignatureRecord?> GetLastForRecordAsync(Guid recordId, CancellationToken ct = default);

    /// <exception cref="Exceptions.ConflictException">The signature is already revoked.</exception>
    Task AddRevocationAsync(SignatureRevocation revocation, CancellationToken ct = default);

    Task<IReadOnlyList<SignatureRevocation>> GetRevocationsAsync(IReadOnlyCollection<Guid> signatureIds, CancellationToken ct = default);
}

public interface ISecurityStore
{
    Task AddAttemptAsync(SigningAttempt attempt, CancellationToken ct = default);

    Task<IReadOnlyList<SigningAttempt>> GetAttemptsSinceAsync(string userId, DateTime sinceUtc, CancellationToken ct = default);

    Task AddAlertAsync(SecurityAlert alert, CancellationToken ct = default);

    Task<IReadOnlyList<SecurityAlert>> ListAlertsAsync(AlertSeverity? minimumSeverity, DateTime? fromUtc, int skip, int take, CancellationToken ct = default);
}

/// <summary>Lets a service write to several stores atomically around a call to RecordAuditTrails.</summary>
public interface IUnitOfWork
{
    Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken ct = default);
}

public interface IUnitOfWorkTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct = default);
}
