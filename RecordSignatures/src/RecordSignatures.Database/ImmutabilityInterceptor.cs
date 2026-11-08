using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RecordSignatures.Core.Exceptions;
using RecordSignatures.Database.Entities;

namespace RecordSignatures.Database;

/// <summary>
/// Application-level guard that complements the PostgreSQL triggers: signatures, revocations, attempts and alerts are
/// insert-only; signers can never be deleted and their identity columns never change, so an identity cannot be reassigned.
/// </summary>
public sealed class ImmutabilityInterceptor : SaveChangesInterceptor
{
    private static readonly string[] SignerIdentityColumns =
    [
        nameof(SignerEntity.Id), nameof(SignerEntity.UserId), nameof(SignerEntity.Username), nameof(SignerEntity.EmployeeId),
        nameof(SignerEntity.EnrolledUtc), nameof(SignerEntity.EnrolledByUserId), nameof(SignerEntity.EnrolledByUsername),
    ];

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
            var appendOnly = entry.Entity is SignatureEntity or SignatureRevocationEntity or SigningAttemptEntity or SecurityAlertEntity;
            if (appendOnly && entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new ConflictException($"{entry.Entity.GetType().Name} rows are append-only and cannot be {(entry.State == EntityState.Deleted ? "deleted" : "modified")}.");
            }

            if (entry.Entity is SignerEntity)
            {
                if (entry.State == EntityState.Deleted)
                {
                    throw new ConflictException("Signer rows cannot be deleted: an electronic signature identity is never reassigned.");
                }
                if (entry.State == EntityState.Modified && ChangedIdentityColumn(entry) is { } column)
                {
                    throw new ConflictException($"The signer's {column} cannot be changed: an electronic signature identity is never reassigned.");
                }
            }
        }
    }

    private static string? ChangedIdentityColumn(EntityEntry entry) =>
        SignerIdentityColumns.FirstOrDefault(name => entry.Property(name).IsModified);
}
