using Microsoft.EntityFrameworkCore;
using RecordAuditTrails.Models;

namespace RecordAuditTrails.Database;

public sealed record AuditTrailFilter(
    string? Username = null,
    AuditAction? Action = null,
    string? FieldName = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    Guid? RecordId = null,
    int Skip = 0,
    int Take = 100);

public interface IUnitOfWork
{
    /// <summary>Commits all pending changes atomically (record change and its audit entries succeed or fail together).</summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface IRecordRepository
{
    void Add(ElectronicRecord record);

    Task<ElectronicRecord?> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<ElectronicRecord>> ListAsync(string? recordType, bool includeDeleted, int skip, int take, CancellationToken ct = default);

    Task<IReadOnlyList<ElectronicRecord>> ListRetentionExpiredAsync(DateTime nowUtc, int skip, int take, CancellationToken ct = default);

    Task<IReadOnlyList<Guid>> ListIdsAsync(CancellationToken ct = default);
}

public interface IAuditTrailRepository
{
    void AddRange(IEnumerable<AuditTrailEntry> entries);

    Task<AuditTrailEntry?> GetLastAsync(Guid recordId, CancellationToken ct = default);

    Task<IReadOnlyList<AuditTrailEntry>> GetChainAsync(Guid recordId, CancellationToken ct = default);

    Task<IReadOnlyList<AuditTrailEntry>> SearchAsync(AuditTrailFilter filter, CancellationToken ct = default);
}

public interface IAccessLogRepository
{
    void Add(AccessLogEntry entry);

    Task<IReadOnlyList<AccessLogEntry>> ListAsync(string subjectType, Guid subjectId, int skip, int take, CancellationToken ct = default);
}

public interface IDocumentRepository
{
    void Add(SystemDocument document);

    void AddRevision(DocumentRevision revision);

    Task<SystemDocument?> GetAsync(Guid id, CancellationToken ct = default);

    Task<bool> NumberExistsAsync(string documentNumber, CancellationToken ct = default);

    Task<IReadOnlyList<SystemDocument>> ListAsync(CancellationToken ct = default);

    Task<IReadOnlyList<DocumentRevision>> GetRevisionsAsync(Guid documentId, CancellationToken ct = default);

    Task<DocumentRevision?> GetLatestRevisionAsync(Guid documentId, CancellationToken ct = default);
}

public sealed class RecordRepository(AuditDbContext db) : IRecordRepository
{
    public void Add(ElectronicRecord record) => db.Records.Add(record);

    public Task<ElectronicRecord?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.Records.FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<ElectronicRecord>> ListAsync(string? recordType, bool includeDeleted, int skip, int take, CancellationToken ct = default)
    {
        var query = db.Records.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(recordType))
        {
            query = query.Where(r => r.RecordType == recordType);
        }
        if (!includeDeleted)
        {
            query = query.Where(r => !r.IsDeleted);
        }
        return await query.OrderBy(r => r.CreatedUtc).ThenBy(r => r.Id).Skip(skip).Take(take).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ElectronicRecord>> ListRetentionExpiredAsync(DateTime nowUtc, int skip, int take, CancellationToken ct = default) =>
        await db.Records.AsNoTracking()
            .Where(r => r.RetainUntilUtc <= nowUtc)
            .OrderBy(r => r.RetainUntilUtc).ThenBy(r => r.Id)
            .Skip(skip).Take(take).ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> ListIdsAsync(CancellationToken ct = default) =>
        // Audit trails are the source of truth, so records whose row is somehow missing are still verified.
        await db.AuditTrail.AsNoTracking().Select(a => a.RecordId).Distinct().ToListAsync(ct);
}

public sealed class AuditTrailRepository(AuditDbContext db) : IAuditTrailRepository
{
    public void AddRange(IEnumerable<AuditTrailEntry> entries) => db.AuditTrail.AddRange(entries);

    public Task<AuditTrailEntry?> GetLastAsync(Guid recordId, CancellationToken ct = default) =>
        db.AuditTrail.AsNoTracking().Where(a => a.RecordId == recordId).OrderByDescending(a => a.Sequence).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<AuditTrailEntry>> GetChainAsync(Guid recordId, CancellationToken ct = default) =>
        await db.AuditTrail.AsNoTracking().Where(a => a.RecordId == recordId).OrderBy(a => a.Sequence).ToListAsync(ct);

    public async Task<IReadOnlyList<AuditTrailEntry>> SearchAsync(AuditTrailFilter f, CancellationToken ct = default)
    {
        var query = db.AuditTrail.AsNoTracking();
        if (f.RecordId is { } recordId)
        {
            query = query.Where(a => a.RecordId == recordId);
        }
        if (!string.IsNullOrWhiteSpace(f.Username))
        {
            query = query.Where(a => a.Username == f.Username);
        }
        if (f.Action is { } action)
        {
            query = query.Where(a => a.Action == action);
        }
        if (!string.IsNullOrWhiteSpace(f.FieldName))
        {
            query = query.Where(a => a.FieldName == f.FieldName);
        }
        if (f.FromUtc is { } from)
        {
            query = query.Where(a => a.TimestampUtc >= from);
        }
        if (f.ToUtc is { } to)
        {
            query = query.Where(a => a.TimestampUtc <= to);
        }
        return await query.OrderBy(a => a.Id).Skip(f.Skip).Take(f.Take).ToListAsync(ct);
    }
}

public sealed class AccessLogRepository(AuditDbContext db) : IAccessLogRepository
{
    public void Add(AccessLogEntry entry) => db.AccessLog.Add(entry);

    public async Task<IReadOnlyList<AccessLogEntry>> ListAsync(string subjectType, Guid subjectId, int skip, int take, CancellationToken ct = default) =>
        await db.AccessLog.AsNoTracking()
            .Where(a => a.SubjectType == subjectType && a.SubjectId == subjectId)
            .OrderBy(a => a.Id).Skip(skip).Take(take).ToListAsync(ct);
}

public sealed class DocumentRepository(AuditDbContext db) : IDocumentRepository
{
    public void Add(SystemDocument document) => db.Documents.Add(document);

    public void AddRevision(DocumentRevision revision) => db.DocumentRevisions.Add(revision);

    public Task<SystemDocument?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);

    public Task<bool> NumberExistsAsync(string documentNumber, CancellationToken ct = default) =>
        db.Documents.AnyAsync(d => d.DocumentNumber == documentNumber, ct);

    public async Task<IReadOnlyList<SystemDocument>> ListAsync(CancellationToken ct = default) =>
        await db.Documents.AsNoTracking().OrderBy(d => d.DocumentNumber).ToListAsync(ct);

    public async Task<IReadOnlyList<DocumentRevision>> GetRevisionsAsync(Guid documentId, CancellationToken ct = default) =>
        await db.DocumentRevisions.AsNoTracking().Where(r => r.DocumentId == documentId).OrderBy(r => r.RevisionNumber).ToListAsync(ct);

    public Task<DocumentRevision?> GetLatestRevisionAsync(Guid documentId, CancellationToken ct = default) =>
        db.DocumentRevisions.AsNoTracking().Where(r => r.DocumentId == documentId).OrderByDescending(r => r.RevisionNumber).FirstOrDefaultAsync(ct);
}

public sealed class UnitOfWork(AuditDbContext db) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            db.ChangeTracker.Clear();
            throw new Core.Exceptions.ConcurrencyConflictException($"The data was changed by another user. Reload and retry. ({ex.Entries.Count} conflicting rows)");
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            throw new Core.Exceptions.ConcurrencyConflictException("A concurrent change conflicted with this one. Reload and retry.");
        }
    }

    // Postgres SQLSTATE 23505 (unique_violation); SQLite extended codes 2067 (UNIQUE) and 1555 (PRIMARYKEY).
    internal static bool IsUniqueViolation(DbUpdateException ex)
    {
        for (Exception? e = ex.InnerException; e is not null; e = e.InnerException)
        {
            var type = e.GetType();
            var sqlState = type.GetProperty("SqlState")?.GetValue(e) as string;
            if (sqlState == "23505")
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
