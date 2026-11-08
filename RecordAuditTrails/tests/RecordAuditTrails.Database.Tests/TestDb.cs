using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RecordAuditTrails.Core.Auditing;
using RecordAuditTrails.Core.Hashing;
using RecordAuditTrails.Database;
using RecordAuditTrails.Models;

namespace RecordAuditTrails.Database.Tests;

/// <summary>An isolated in-memory SQLite database with the real model and the real immutability guard.</summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public TestDb()
    {
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public AuditDbContext NewContext() => new(new DbContextOptionsBuilder<AuditDbContext>()
        .UseSqlite(_connection)
        .AddInterceptors(new ImmutabilityInterceptor())
        .Options);

    public void Dispose() => _connection.Dispose();

    public static readonly DateTime T0 = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    public static ElectronicRecord Record(string type = "BatchRecord", string title = "Batch 1", DateTime? created = null, DateTime? retainUntil = null) => new()
    {
        Id = Guid.NewGuid(), RecordType = type, Title = title, DataJson = "{}", Version = 1,
        RetainUntilUtc = retainUntil ?? T0.AddYears(7), CreatedUtc = created ?? T0, CreatedBy = "alice",
        ModifiedUtc = created ?? T0, ModifiedBy = "alice",
    };

    public static AuditTrailEntry Entry(Guid recordId, long sequence, string previousHash, string user = "alice",
        AuditAction action = AuditAction.Modify, string field = "Title", DateTime? at = null)
    {
        var e = new AuditTrailEntry
        {
            RecordId = recordId, ChangeSetId = Guid.NewGuid(), Sequence = sequence, RecordVersion = 1, Action = action,
            FieldName = field, OldValue = "a", NewValue = "b", Reason = "testing reason", UserId = "u-" + user, Username = user,
            Role = "LabTechnician", TimestampUtc = at ?? T0.AddMinutes(sequence), PreviousHash = previousHash,
        };
        e.Hash = AuditHash.ForEntry(e);
        return e;
    }

    public static string Genesis => CanonicalHasher.GenesisHash;
}
