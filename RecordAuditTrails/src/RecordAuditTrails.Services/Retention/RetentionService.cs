using RecordAuditTrails.Core.Exceptions;
using RecordAuditTrails.Core.Hashing;
using RecordAuditTrails.Core.Retention;
using RecordAuditTrails.Database;

namespace RecordAuditTrails.Services.Retention;

public sealed record RetentionStatus(
    Guid RecordId,
    string RecordType,
    DateTime RetainUntilUtc,
    DateTime AuditTrailRetainUntilUtc,
    bool RetentionExpired,
    bool IsDeleted);

public interface IRetentionService
{
    Task<RetentionStatus> GetStatusAsync(Guid recordId, CancellationToken ct = default);

    /// <summary>Records whose retention period has ended and which may now be reviewed for disposition. Nothing is deleted automatically.</summary>
    Task<IReadOnlyList<RetentionStatus>> ListExpiredAsync(int skip, int take, CancellationToken ct = default);
}

public sealed class RetentionService(IRecordRepository records, TimeProvider clock) : IRetentionService
{
    public async Task<RetentionStatus> GetStatusAsync(Guid recordId, CancellationToken ct = default)
    {
        var record = await records.GetAsync(recordId, ct) ?? throw new NotFoundException($"Record {recordId} was not found.");
        return ToStatus(record, Now());
    }

    public async Task<IReadOnlyList<RetentionStatus>> ListExpiredAsync(int skip, int take, CancellationToken ct = default)
    {
        var now = Now();
        var (s, t) = Paging.Normalize(skip, take);
        return (await records.ListRetentionExpiredAsync(now, s, t, ct)).Select(r => ToStatus(r, now)).ToList();
    }

    private DateTime Now() => TimestampPrecision.ToStorage(clock.GetUtcNow());

    private static RetentionStatus ToStatus(Models.ElectronicRecord r, DateTime now) => new(
        r.Id,
        r.RecordType,
        r.RetainUntilUtc,
        // The audit trail is retained at least as long as the record it documents.
        r.RetainUntilUtc,
        RetentionPolicy.IsExpired(r.RetainUntilUtc, now),
        r.IsDeleted);
}
