using RecordSignatures.Core.Abstractions;
using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Models;

namespace RecordSignatures.TestSupport;

public sealed class InMemoryData
{
    public List<Signer> Signers { get; set; } = [];
    public List<SignatureRecord> Signatures { get; set; } = [];
    public List<SignatureRevocation> Revocations { get; set; } = [];
    public List<SigningAttempt> Attempts { get; set; } = [];
    public List<SecurityAlert> Alerts { get; set; } = [];

    public bool FailSignatureAdd { get; set; }
    public Exception? SignatureAddException { get; set; }
}

public sealed class InMemorySignerStore(InMemoryData data) : ISignerStore
{
    private static Signer Clone(Signer s)
    {
        var c = new Signer();
        foreach (var p in typeof(Signer).GetProperties().Where(p => p.CanWrite))
        {
            p.SetValue(c, p.GetValue(s));
        }
        return c;
    }

    public Task<Signer?> FindByUserIdAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult(data.Signers.FirstOrDefault(s => s.UserId == userId) is { } s ? Clone(s) : null);

    public Task<Signer?> FindByUsernameAsync(string username, CancellationToken ct = default) =>
        Task.FromResult(data.Signers.FirstOrDefault(s => s.Username == username.ToLowerInvariant()) is { } s ? Clone(s) : null);

    public Task<bool> EmployeeIdInUseAsync(string employeeId, CancellationToken ct = default) =>
        Task.FromResult(data.Signers.Any(s => s.EmployeeId == employeeId && s.Status != SignerStatus.Revoked));

    public Task<IReadOnlyList<Signer>> ListAsync(SignerStatus? status, bool reviewDueOnly, DateTime nowUtc, int skip, int take, CancellationToken ct = default)
    {
        var q = data.Signers.AsEnumerable();
        if (status is { } st)
        {
            q = q.Where(s => s.Status == st);
        }
        if (reviewDueOnly)
        {
            q = q.Where(s => s.NextReviewDueUtc <= nowUtc && s.Status != SignerStatus.Revoked);
        }
        return Task.FromResult<IReadOnlyList<Signer>>(q.OrderBy(s => s.PrintedName).Skip(skip).Take(take).Select(Clone).ToList());
    }

    public Task AddAsync(Signer signer, CancellationToken ct = default)
    {
        if (data.Signers.Any(s => s.UserId == signer.UserId || s.Username == signer.Username))
        {
            throw new ConflictException("duplicate signer");
        }
        signer.Version = 1;
        data.Signers.Add(Clone(signer));
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Signer signer, CancellationToken ct = default)
    {
        var stored = data.Signers.FirstOrDefault(s => s.Id == signer.Id) ?? throw new NotFoundException("gone");
        if (stored.Version != signer.Version)
        {
            throw new ConflictException("stale");
        }
        signer.Version++;
        data.Signers[data.Signers.IndexOf(stored)] = Clone(signer);
        return Task.CompletedTask;
    }
}

public sealed class InMemorySignatureStore(InMemoryData data) : ISignatureStore
{
    public Task AddAsync(SignatureRecord signature, CancellationToken ct = default)
    {
        if (data.SignatureAddException is { } ex)
        {
            throw ex;
        }
        if (data.FailSignatureAdd || data.Signatures.Any(s => s.RecordId == signature.RecordId && s.Sequence == signature.Sequence))
        {
            throw new ConflictException("position taken");
        }
        data.Signatures.Add(signature);
        return Task.CompletedTask;
    }

    public Task<SignatureRecord?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(data.Signatures.FirstOrDefault(s => s.Id == id));

    public Task<IReadOnlyList<SignatureRecord>> ListForRecordAsync(Guid recordId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SignatureRecord>>(data.Signatures.Where(s => s.RecordId == recordId).OrderBy(s => s.Sequence).ToList());

    public Task<SignatureRecord?> GetLastForRecordAsync(Guid recordId, CancellationToken ct = default) =>
        Task.FromResult(data.Signatures.Where(s => s.RecordId == recordId).OrderByDescending(s => s.Sequence).FirstOrDefault());

    public Task AddRevocationAsync(SignatureRevocation revocation, CancellationToken ct = default)
    {
        if (data.Revocations.Any(r => r.SignatureId == revocation.SignatureId))
        {
            throw new ConflictException("already revoked");
        }
        data.Revocations.Add(revocation);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SignatureRevocation>> GetRevocationsAsync(IReadOnlyCollection<Guid> signatureIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SignatureRevocation>>(data.Revocations.Where(r => signatureIds.Contains(r.SignatureId)).ToList());
}

public sealed class InMemorySecurityStore(InMemoryData data) : ISecurityStore
{
    public Task AddAttemptAsync(SigningAttempt attempt, CancellationToken ct = default)
    {
        data.Attempts.Add(attempt);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SigningAttempt>> GetAttemptsSinceAsync(string userId, DateTime sinceUtc, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SigningAttempt>>(data.Attempts.Where(a => a.UserId == userId && a.AttemptedUtc >= sinceUtc).ToList());

    public Task AddAlertAsync(SecurityAlert alert, CancellationToken ct = default)
    {
        data.Alerts.Add(alert);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SecurityAlert>> ListAlertsAsync(AlertSeverity? minimumSeverity, DateTime? fromUtc, int skip, int take, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SecurityAlert>>(data.Alerts
            .Where(a => (minimumSeverity is null || a.Severity >= minimumSeverity) && (fromUtc is null || a.RaisedUtc >= fromUtc))
            .OrderByDescending(a => a.RaisedUtc).Skip(skip).Take(take).ToList());
}

/// <summary>Snapshots the data on begin and restores it when disposed without a commit, like a real rollback.</summary>
public sealed class InMemoryUnitOfWork(InMemoryData data) : IUnitOfWork
{
    public int Commits { get; private set; }

    public int Rollbacks { get; private set; }

    public Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken ct = default) =>
        Task.FromResult<IUnitOfWorkTransaction>(new Tx(this, data));

    private sealed class Tx(InMemoryUnitOfWork owner, InMemoryData data) : IUnitOfWorkTransaction
    {
        private readonly List<Signer> _signers = [.. data.Signers];
        private readonly List<SignatureRecord> _signatures = [.. data.Signatures];
        private readonly List<SignatureRevocation> _revocations = [.. data.Revocations];
        private bool _committed;

        public Task CommitAsync(CancellationToken ct = default)
        {
            _committed = true;
            owner.Commits++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                owner.Rollbacks++;
                data.Signers = _signers;
                data.Signatures = _signatures;
                data.Revocations = _revocations;
            }
            return ValueTask.CompletedTask;
        }
    }
}
