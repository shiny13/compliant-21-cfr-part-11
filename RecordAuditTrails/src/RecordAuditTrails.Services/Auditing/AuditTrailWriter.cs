using RecordAuditTrails.Core.Auditing;
using RecordAuditTrails.Core.Hashing;
using RecordAuditTrails.Database;
using RecordAuditTrails.Models;

namespace RecordAuditTrails.Services.Auditing;

public interface IAuditTrailWriter
{
    /// <summary>
    /// Appends one chained, time-stamped entry per field change. Entries are only added to the unit of work; the
    /// caller commits them in the same transaction as the record change itself.
    /// </summary>
    Task<IReadOnlyList<AuditTrailEntry>> AppendAsync(
        Guid recordId,
        int recordVersion,
        AuditAction action,
        IReadOnlyList<FieldChange> changes,
        string reason,
        Actor actor,
        DateTime timestampUtc,
        CancellationToken ct = default);
}

public sealed class AuditTrailWriter(IAuditTrailRepository trail) : IAuditTrailWriter
{
    public async Task<IReadOnlyList<AuditTrailEntry>> AppendAsync(
        Guid recordId,
        int recordVersion,
        AuditAction action,
        IReadOnlyList<FieldChange> changes,
        string reason,
        Actor actor,
        DateTime timestampUtc,
        CancellationToken ct = default)
    {
        var last = await trail.GetLastAsync(recordId, ct);
        var previousHash = last?.Hash ?? CanonicalHasher.GenesisHash;
        var sequence = last?.Sequence ?? 0;
        var changeSetId = Guid.NewGuid();

        var entries = new List<AuditTrailEntry>(changes.Count);
        foreach (var change in changes)
        {
            var entry = new AuditTrailEntry
            {
                RecordId = recordId,
                ChangeSetId = changeSetId,
                Sequence = ++sequence,
                RecordVersion = recordVersion,
                Action = action,
                FieldName = change.Field,
                OldValue = change.OldValue,
                NewValue = change.NewValue,
                Reason = reason,
                UserId = actor.UserId,
                Username = actor.Username,
                Role = actor.Role.ToString(),
                TimestampUtc = timestampUtc,
                PreviousHash = previousHash,
            };
            entry.Hash = AuditHash.ForEntry(entry);
            previousHash = entry.Hash;
            entries.Add(entry);
        }

        trail.AddRange(entries);
        return entries;
    }
}
