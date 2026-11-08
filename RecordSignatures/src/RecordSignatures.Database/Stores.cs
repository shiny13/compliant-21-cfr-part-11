using Microsoft.EntityFrameworkCore;
using RecordSignatures.Core.Abstractions;
using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Models;
using RecordSignatures.Database.Entities;

namespace RecordSignatures.Database;

internal static class SaveHelper
{
    /// <summary>Saves, turning a unique-index violation into the business <see cref="ConflictException"/> supplied.</summary>
    public static async Task SaveAsync(SignaturesDbContext db, string conflictMessage, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            db.ChangeTracker.Clear();
            throw new ConflictException("The data was changed by someone else. Reload and retry.", ex);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            throw new ConflictException(conflictMessage, ex);
        }
    }

    // Postgres SQLSTATE 23505 (unique_violation); SQLite extended codes 2067 (UNIQUE) and 1555 (PRIMARYKEY).
    // SQLite's plain code 19 covers every constraint type, so NOT NULL and CHECK failures must not be mistaken for conflicts.
    internal static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex.InnerException; e is not null; e = e.InnerException)
        {
            var type = e.GetType();
            if (type.GetProperty("SqlState")?.GetValue(e) as string == "23505")
            {
                return true;
            }
            if (type.GetProperty("SqliteExtendedErrorCode")?.GetValue(e) is int code && code is 2067 or 1555)
            {
                return true;
            }
        }
        return false;
    }
}

public sealed class SignerStore(SignaturesDbContext db) : ISignerStore
{
    public async Task<Signer?> FindByUserIdAsync(string userId, CancellationToken ct = default) =>
        (await db.Signers.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct))?.ToDomain();

    public async Task<Signer?> FindByUsernameAsync(string username, CancellationToken ct = default)
    {
        var normalized = username.ToLowerInvariant();
        return (await db.Signers.AsNoTracking().FirstOrDefaultAsync(s => s.Username == normalized, ct))?.ToDomain();
    }

    public Task<bool> EmployeeIdInUseAsync(string employeeId, CancellationToken ct = default) =>
        db.Signers.AnyAsync(s => s.EmployeeId == employeeId && s.Status != SignaturesDbContext.RevokedStatus, ct);

    public async Task<IReadOnlyList<Signer>> ListAsync(SignerStatus? status, bool reviewDueOnly, DateTime nowUtc, int skip, int take, CancellationToken ct = default)
    {
        var query = db.Signers.AsNoTracking().AsQueryable();
        if (status is { } s)
        {
            var name = s.ToString();
            query = query.Where(x => x.Status == name);
        }
        if (reviewDueOnly)
        {
            query = query.Where(x => x.NextReviewDueUtc <= nowUtc && x.Status != SignaturesDbContext.RevokedStatus);
        }
        var rows = await query.OrderBy(x => x.PrintedName).ThenBy(x => x.UserId).Skip(skip).Take(take).ToListAsync(ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task AddAsync(Signer signer, CancellationToken ct = default)
    {
        var entity = signer.ToEntity();
        db.Signers.Add(entity);
        await SaveHelper.SaveAsync(db, "The user id, identification code or employee id is already enrolled.", ct);
        signer.Version = entity.Version;
    }

    public async Task UpdateAsync(Signer signer, CancellationToken ct = default)
    {
        var entity = await db.Signers.FirstOrDefaultAsync(s => s.Id == signer.Id, ct)
            ?? throw new NotFoundException("The signer no longer exists.");
        if (entity.Version != signer.Version)
        {
            throw new ConflictException("The signer was changed by someone else. Reload and retry.");
        }
        signer.CopyMutableTo(entity);
        entity.Version++;
        await SaveHelper.SaveAsync(db, "The change conflicts with an existing enrollment.", ct);
        signer.Version = entity.Version;
    }
}

public sealed class SignatureStore(SignaturesDbContext db) : ISignatureStore
{
    public async Task AddAsync(SignatureRecord signature, CancellationToken ct = default)
    {
        db.Signatures.Add(signature.ToEntity());
        await SaveHelper.SaveAsync(db, "Another signature already holds this position in the record's chain.", ct);
    }

    public async Task<SignatureRecord?> GetAsync(Guid id, CancellationToken ct = default) =>
        (await db.Signatures.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct))?.ToDomain();

    public async Task<IReadOnlyList<SignatureRecord>> ListForRecordAsync(Guid recordId, CancellationToken ct = default) =>
        (await db.Signatures.AsNoTracking().Where(s => s.RecordId == recordId).OrderBy(s => s.Sequence).ToListAsync(ct))
        .Select(s => s.ToDomain()).ToList();

    public async Task<SignatureRecord?> GetLastForRecordAsync(Guid recordId, CancellationToken ct = default) =>
        (await db.Signatures.AsNoTracking().Where(s => s.RecordId == recordId).OrderByDescending(s => s.Sequence).FirstOrDefaultAsync(ct))?.ToDomain();

    public async Task AddRevocationAsync(SignatureRevocation revocation, CancellationToken ct = default)
    {
        db.SignatureRevocations.Add(revocation.ToEntity());
        await SaveHelper.SaveAsync(db, "The signature has already been revoked.", ct);
    }

    public async Task<IReadOnlyList<SignatureRevocation>> GetRevocationsAsync(IReadOnlyCollection<Guid> signatureIds, CancellationToken ct = default) =>
        (await db.SignatureRevocations.AsNoTracking().Where(r => signatureIds.Contains(r.SignatureId)).ToListAsync(ct))
        .Select(r => r.ToDomain()).ToList();
}

public sealed class SecurityStore(SignaturesDbContext db) : ISecurityStore
{
    public async Task AddAttemptAsync(SigningAttempt attempt, CancellationToken ct = default)
    {
        db.SigningAttempts.Add(attempt.ToEntity());
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SigningAttempt>> GetAttemptsSinceAsync(string userId, DateTime sinceUtc, CancellationToken ct = default) =>
        (await db.SigningAttempts.AsNoTracking().Where(a => a.UserId == userId && a.AttemptedUtc >= sinceUtc)
            .OrderBy(a => a.AttemptedUtc).ToListAsync(ct)).Select(a => a.ToDomain()).ToList();

    public async Task AddAlertAsync(SecurityAlert alert, CancellationToken ct = default)
    {
        db.SecurityAlerts.Add(alert.ToEntity());
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SecurityAlert>> ListAlertsAsync(AlertSeverity? minimumSeverity, DateTime? fromUtc, int skip, int take, CancellationToken ct = default)
    {
        var query = db.SecurityAlerts.AsNoTracking().AsQueryable();
        if (minimumSeverity is { } min)
        {
            var names = Enum.GetValues<AlertSeverity>().Where(v => v >= min).Select(v => v.ToString()).ToList();
            query = query.Where(a => names.Contains(a.Severity));
        }
        if (fromUtc is { } from)
        {
            query = query.Where(a => a.RaisedUtc >= from);
        }
        var rows = await query.OrderByDescending(a => a.RaisedUtc).Skip(skip).Take(take).ToListAsync(ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }
}

public sealed class UnitOfWork(SignaturesDbContext db) : IUnitOfWork
{
    public async Task<IUnitOfWorkTransaction> BeginTransactionAsync(CancellationToken ct = default) =>
        new Transaction(await db.Database.BeginTransactionAsync(ct));

    private sealed class Transaction(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction inner) : IUnitOfWorkTransaction
    {
        public Task CommitAsync(CancellationToken ct = default) => inner.CommitAsync(ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
