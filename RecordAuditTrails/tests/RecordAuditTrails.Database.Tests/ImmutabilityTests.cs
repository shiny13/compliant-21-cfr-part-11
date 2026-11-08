using Microsoft.EntityFrameworkCore;
using RecordAuditTrails.Core.Exceptions;
using RecordAuditTrails.Models;

namespace RecordAuditTrails.Database.Tests;

public class ImmutabilityInterceptorTests
{
    private static async Task<(TestDb Test, Guid RecordId)> SeedAsync()
    {
        var test = new TestDb();
        var record = TestDb.Record();
        await using var db = test.NewContext();
        db.Records.Add(record);
        db.AuditTrail.Add(TestDb.Entry(record.Id, 1, TestDb.Genesis));
        db.AccessLog.Add(new AccessLogEntry { SubjectType = "Record", SubjectId = record.Id, UserId = "u", Username = "a", Role = "r", TimestampUtc = TestDb.T0 });
        var doc = new SystemDocument { Id = Guid.NewGuid(), DocumentNumber = "D", Title = "t", Category = "c", MinimumReadRole = "Reviewer", CurrentRevision = 1, CreatedUtc = TestDb.T0, CreatedBy = "a" };
        db.Documents.Add(doc);
        db.DocumentRevisions.Add(new DocumentRevision { DocumentId = doc.Id, RevisionNumber = 1, Title = "t", Content = "c", ChangeDescription = "initial", AuthorId = "u", Author = "a", CreatedUtc = TestDb.T0, PreviousHash = TestDb.Genesis, Hash = "h" });
        await db.SaveChangesAsync();
        return (test, record.Id);
    }

    [Fact]
    public async Task ModifyingAuditEntry_IsRefused()
    {
        var (test, _) = await SeedAsync();
        using var _t = test;
        await using var db = test.NewContext();
        (await db.AuditTrail.FirstAsync()).NewValue = "rewritten history";

        var ex = await Assert.ThrowsAsync<ImmutableDataException>(() => db.SaveChangesAsync());
        Assert.Contains("append-only", ex.Message);
    }

    [Fact]
    public async Task DeletingAuditEntry_IsRefused()
    {
        var (test, _) = await SeedAsync();
        using var _t = test;
        await using var db = test.NewContext();
        db.AuditTrail.Remove(await db.AuditTrail.FirstAsync());

        var ex = await Assert.ThrowsAsync<ImmutableDataException>(() => db.SaveChangesAsync());
        Assert.Contains("deleted", ex.Message);
    }

    [Fact]
    public async Task ModifyingAccessLogOrRevision_IsRefused()
    {
        var (test, _) = await SeedAsync();
        using var _t = test;
        await using (var db = test.NewContext())
        {
            (await db.AccessLog.FirstAsync()).Details = "edited";
            await Assert.ThrowsAsync<ImmutableDataException>(() => db.SaveChangesAsync());
        }
        await using (var db = test.NewContext())
        {
            (await db.DocumentRevisions.FirstAsync()).Content = "edited";
            await Assert.ThrowsAsync<ImmutableDataException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task PhysicallyDeletingRecordOrDocument_IsRefused()
    {
        var (test, _) = await SeedAsync();
        using var _t = test;
        await using (var db = test.NewContext())
        {
            db.Records.Remove(await db.Records.FirstAsync());
            await Assert.ThrowsAsync<ImmutableDataException>(() => db.SaveChangesAsync());
        }
        await using (var db = test.NewContext())
        {
            db.Documents.Remove(await db.Documents.FirstAsync());
            await Assert.ThrowsAsync<ImmutableDataException>(() => db.SaveChangesAsync());
        }
    }

    [Fact]
    public async Task SynchronousSaveChanges_IsGuardedToo()
    {
        var (test, _) = await SeedAsync();
        using var _t = test;
        await using var db = test.NewContext();
        db.AuditTrail.First().Reason = "changed";

        Assert.Throws<ImmutableDataException>(() => db.SaveChanges());
    }

    [Fact]
    public async Task UpdatingAMutableRecord_AndInsertingEntries_IsAllowed()
    {
        var (test, recordId) = await SeedAsync();
        using var _t = test;
        await using var db = test.NewContext();
        var record = await db.Records.SingleAsync(r => r.Id == recordId);
        record.Title = "new title";
        record.Version++;
        db.AuditTrail.Add(TestDb.Entry(recordId, 2, "prev"));

        Assert.Equal(2, await db.SaveChangesAsync());
    }

    [Fact]
    public void SynchronousSaveChanges_AllowsAppendOnlyInserts()
    {
        using var test = new TestDb();
        using var db = test.NewContext();
        db.AuditTrail.Add(TestDb.Entry(Guid.NewGuid(), 1, TestDb.Genesis));

        Assert.Equal(1, db.SaveChanges());
    }

    [Fact]
    public void Check_IgnoresNullContext() => ImmutabilityInterceptor.Check(null);
}

public class UtcDateTimeConverterTests
{
    [Fact]
    public void ConvertsToUtcOnWriteAndMarksUtcOnRead()
    {
        var converter = new UtcDateTimeConverter();
        var toProvider = converter.ConvertToProviderExpression.Compile();
        var fromProvider = converter.ConvertFromProviderExpression.Compile();

        var local = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);
        Assert.Equal(DateTimeKind.Utc, toProvider(local).Kind);
        Assert.Equal(local.ToUniversalTime(), toProvider(local));
        var utc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(utc, toProvider(utc));
        Assert.Equal(DateTimeKind.Utc, fromProvider(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Unspecified)).Kind);
    }
}
