using RecordAuditTrails.Core.Auditing;
using RecordAuditTrails.Core.Exceptions;
using RecordAuditTrails.Database;
using RecordAuditTrails.Models;

namespace RecordAuditTrails.Services.Auditing;

public sealed record AuditTrailQuery(
    string? Username = null,
    AuditAction? Action = null,
    string? FieldName = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    int Skip = 0,
    int Take = Paging.DefaultPageSize);

public sealed record RecordIntegrityResult(Guid RecordId, IntegrityResult Result);

public sealed record SystemIntegrityReport(bool IsValid, int RecordsChecked, IReadOnlyList<RecordIntegrityResult> Failures);

public interface IAuditTrailService
{
    /// <summary>The full audit trail of one record, oldest first, available for review at any time (including deleted records).</summary>
    Task<IReadOnlyList<AuditTrailEntry>> GetForRecordAsync(Guid recordId, AuditTrailQuery query, CancellationToken ct = default);

    Task<IReadOnlyList<AuditTrailEntry>> SearchAsync(AuditTrailQuery query, CancellationToken ct = default);

    Task<IntegrityResult> VerifyRecordAsync(Guid recordId, CancellationToken ct = default);

    Task<SystemIntegrityReport> VerifyAllAsync(CancellationToken ct = default);
}

public sealed class AuditTrailService(IAuditTrailRepository trail, IRecordRepository records) : IAuditTrailService
{
    public async Task<IReadOnlyList<AuditTrailEntry>> GetForRecordAsync(Guid recordId, AuditTrailQuery query, CancellationToken ct = default)
    {
        await EnsureRecordKnownAsync(recordId, ct);
        return await trail.SearchAsync(ToFilter(query, recordId), ct);
    }

    public Task<IReadOnlyList<AuditTrailEntry>> SearchAsync(AuditTrailQuery query, CancellationToken ct = default) =>
        trail.SearchAsync(ToFilter(query, null), ct);

    public async Task<IntegrityResult> VerifyRecordAsync(Guid recordId, CancellationToken ct = default)
    {
        await EnsureRecordKnownAsync(recordId, ct);
        return ChainVerifier.VerifyAuditTrail(await trail.GetChainAsync(recordId, ct));
    }

    public async Task<SystemIntegrityReport> VerifyAllAsync(CancellationToken ct = default)
    {
        var ids = await records.ListIdsAsync(ct);
        var failures = new List<RecordIntegrityResult>();
        foreach (var id in ids)
        {
            var result = ChainVerifier.VerifyAuditTrail(await trail.GetChainAsync(id, ct));
            if (!result.IsValid)
            {
                failures.Add(new RecordIntegrityResult(id, result));
            }
        }
        return new SystemIntegrityReport(failures.Count == 0, ids.Count, failures);
    }

    private async Task EnsureRecordKnownAsync(Guid recordId, CancellationToken ct)
    {
        // A record row may be missing while its trail exists; the trail is the authority.
        if (await records.GetAsync(recordId, ct) is null && await trail.GetLastAsync(recordId, ct) is null)
        {
            throw new NotFoundException($"Record {recordId} was not found.");
        }
    }

    private static AuditTrailFilter ToFilter(AuditTrailQuery q, Guid? recordId)
    {
        var (skip, take) = Paging.Normalize(q.Skip, q.Take);
        return new AuditTrailFilter(q.Username, q.Action, q.FieldName, q.FromUtc, q.ToUtc, recordId, skip, take);
    }
}
