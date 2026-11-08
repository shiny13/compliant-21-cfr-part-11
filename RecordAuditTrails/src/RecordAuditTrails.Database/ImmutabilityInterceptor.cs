using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RecordAuditTrails.Core.Exceptions;
using RecordAuditTrails.Models;

namespace RecordAuditTrails.Database;

/// <summary>
/// Application-level guard that complements the database triggers: audit entries, access log entries and document
/// revisions can only ever be inserted, and records and documents can never be physically deleted.
/// </summary>
public sealed class ImmutabilityInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Check(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(eventData.Context);
        return ValueTask.FromResult(result);
    }

    internal static void Check(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries())
        {
            var appendOnly = entry.Entity is AuditTrailEntry or AccessLogEntry or DocumentRevision;
            var neverDeleted = entry.Entity is ElectronicRecord or SystemDocument;

            if (appendOnly && entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new ImmutableDataException($"{entry.Entity.GetType().Name} rows are append-only and cannot be {(entry.State == EntityState.Deleted ? "deleted" : "modified")}.");
            }
            if (neverDeleted && entry.State == EntityState.Deleted)
            {
                throw new ImmutableDataException($"{entry.Entity.GetType().Name} rows cannot be physically deleted.");
            }
        }
    }
}
