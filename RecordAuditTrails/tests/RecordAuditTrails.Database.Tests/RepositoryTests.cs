using Microsoft.EntityFrameworkCore;
using RecordAuditTrails.Core.Exceptions;
using RecordAuditTrails.Models;

namespace RecordAuditTrails.Database.Tests;

public class RecordRepositoryTests
{
    [Fact]
    public async Task AddGetAndListRecords_FilterByTypeAndDeletedAndPage()
    {
        using var test = new TestDb();
        await using var db = test.NewContext();
        var repo = new RecordRepository(db);
        var a = TestDb.Record("A", "first", TestDb.T0);
        var b = TestDb.Record("B", "second", TestDb.T0.AddHours(1));
        var c = TestDb.Record("A", "third", TestDb.T0.AddHours(2));
        c.IsDeleted = true;
        foreach (var r in new[] { a, b, c }) repo.Add(r);
        await new UnitOfWork(db).SaveChangesAsync();

        Assert.Equal("first", (await repo.GetAsync(a.Id))!.Title);
        Assert.Null(await repo.GetAsync(Guid.NewGuid()));
        Assert.Equal(["first", "second"], (await repo.ListAsync(null, false, 0, 10)).Select(r => r.Title));
        Assert.Equal(["first", "third"], (await repo.ListAsync("A", true, 0, 10)).Select(r => r.Title));
        Assert.Equal(["second"], (await repo.ListAsync(" ", false, 1, 10)).Select(r => r.Title));
        Assert.Single(await repo.ListAsync(null, true, 1, 1));
    }

    [Fact]
    public async Task ListRetentionExpired_ReturnsOnlyRecordsPastRetainUntilOldestFirst()
    {
        using var test = new TestDb();
        await using var db = test.NewContext();
        var repo = new RecordRepository(db);
        var old = TestDb.Record(title: "old", retainUntil: TestDb.T0.AddYears(1));
        var older = TestDb.Record(title: "older", retainUntil: TestDb.T0.AddMonths(1));
        var current = TestDb.Record(title: "current", retainUntil: TestDb.T0.AddYears(9));
        foreach (var r in new[] { old, older, current }) repo.Add(r);
        await new UnitOfWork(db).SaveChangesAsync();

        var expired = await repo.ListRetentionExpiredAsync(TestDb.T0.AddYears(2), 0, 10);

        Assert.Equal(["older", "old"], expired.Select(r => r.Title));
    }

    [Fact]
    public async Task ListIds_ComesFromAuditTrailAndIsDistinct()
    {
        using var test = new TestDb();
        await using var db = test.NewContext();
        var trail = new AuditTrailRepository(db);
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var e1 = TestDb.Entry(id1, 1, TestDb.Genesis);
        trail.AddRange([e1, TestDb.Entry(id1, 2, e1.Hash), TestDb.Entry(id2, 1, TestDb.Genesis)]);
        await new UnitOfWork(db).SaveChangesAsync();

        var ids = await new RecordRepository(db).ListIdsAsync();

        Assert.Equal(new[] { id1, id2 }.Order(), ids.Order());
    }
}

public class AuditTrailRepositoryTests
{
    [Fact]
    public async Task GetLastAndChain_ReturnEntriesInSequenceOrder()
    {
        using var test = new TestDb();
        await using var db = test.NewContext();
        var repo = new AuditTrailRepository(db);
        var id = Guid.NewGuid();
        var e1 = TestDb.Entry(id, 1, TestDb.Genesis);
        var e2 = TestDb.Entry(id, 2, e1.Hash);
        repo.AddRange([e2, e1]);
        await new UnitOfWork(db).SaveChangesAsync();

        Assert.Equal(2, (await repo.GetLastAsync(id))!.Sequence);
        Assert.Null(await repo.GetLastAsync(Guid.NewGuid()));
        Assert.Equal([1L, 2L], (await repo.GetChainAsync(id)).Select(e => e.Sequence));
    }

    [Fact]
    public async Task Search_AppliesEachFilter()
    {
        using var test = new TestDb();
        await using var db = test.NewContext();
        var repo = new AuditTrailRepository(db);
        var r1 = Guid.NewGuid();
        var r2 = Guid.NewGuid();
        repo.AddRange(
        [
            TestDb.Entry(r1, 1, TestDb.Genesis, "alice", AuditAction.Create, "Title", TestDb.T0),
            TestDb.Entry(r2, 1, TestDb.Genesis, "bob", AuditAction.Modify, "pH", TestDb.T0.AddDays(1)),
            TestDb.Entry(r2, 2, "x", "bob", AuditAction.Delete, "IsDeleted", TestDb.T0.AddDays(2)),
        ]);
        await new UnitOfWork(db).SaveChangesAsync();

        Assert.Equal(3, (await repo.SearchAsync(new AuditTrailFilter())).Count);
        Assert.Single(await repo.SearchAsync(new AuditTrailFilter(RecordId: r1)));
        Assert.Equal(2, (await repo.SearchAsync(new AuditTrailFilter(Username: "bob"))).Count);
        Assert.Single(await repo.SearchAsync(new AuditTrailFilter(Action: AuditAction.Delete)));
        Assert.Single(await repo.SearchAsync(new AuditTrailFilter(FieldName: "pH")));
        Assert.Equal(2, (await repo.SearchAsync(new AuditTrailFilter(FromUtc: TestDb.T0.AddDays(1)))).Count);
        Assert.Equal(2, (await repo.SearchAsync(new AuditTrailFilter(ToUtc: TestDb.T0.AddDays(1)))).Count);
        Assert.Single(await repo.SearchAsync(new AuditTrailFilter(FromUtc: TestDb.T0.AddHours(1), ToUtc: TestDb.T0.AddDays(1))));
        Assert.Single(await repo.SearchAsync(new AuditTrailFilter(Skip: 2, Take: 5)));
        Assert.Single(await repo.SearchAsync(new AuditTrailFilter(Take: 1)));
        Assert.Empty(await repo.SearchAsync(new AuditTrailFilter(Username: "  ", FieldName: "", Skip: 10)));
    }

    [Fact]
    public async Task EntryValuesRoundTripExactly_IncludingMicrosecondTimestamps()
    {
        using var test = new TestDb();
        var id = Guid.NewGuid();
        var original = TestDb.Entry(id, 1, TestDb.Genesis, at: TestDb.T0.AddTicks(1234560));
        original.OldValue = null;
        await using (var db = test.NewContext())
        {
            new AuditTrailRepository(db).AddRange([original]);
            await new UnitOfWork(db).SaveChangesAsync();
        }

        await using var read = test.NewContext();
        var loaded = (await new AuditTrailRepository(read).GetChainAsync(id)).Single();

        Assert.Equal(original.TimestampUtc, loaded.TimestampUtc);
        Assert.Equal(DateTimeKind.Utc, loaded.TimestampUtc.Kind);
        Assert.Null(loaded.OldValue);
        Assert.Equal(original.Hash, loaded.Hash);
        Assert.Equal(AuditAction.Modify, loaded.Action);
    }
}

public class AccessLogRepositoryTests
{
    [Fact]
    public async Task List_ReturnsOnlyMatchingSubjectOldestFirstWithPaging()
    {
        using var test = new TestDb();
        await using var db = test.NewContext();
        var repo = new AccessLogRepository(db);
        var subject = Guid.NewGuid();
        AccessLogEntry Make(Guid s, string details) => new()
        {
            SubjectType = AccessSubjects.Document, SubjectId = s, Action = AccessAction.View, UserId = "u", Username = "alice",
            Role = "Reviewer", TimestampUtc = TestDb.T0, Details = details,
        };
        repo.Add(Make(subject, "one"));
        repo.Add(Make(subject, "two"));
        repo.Add(Make(Guid.NewGuid(), "other"));
        await new UnitOfWork(db).SaveChangesAsync();

        Assert.Equal(["one", "two"], (await repo.ListAsync(AccessSubjects.Document, subject, 0, 10)).Select(e => e.Details));
        Assert.Equal(["two"], (await repo.ListAsync(AccessSubjects.Document, subject, 1, 10)).Select(e => e.Details));
        Assert.Empty(await repo.ListAsync(AccessSubjects.Record, subject, 0, 10));
    }
}

public class DocumentRepositoryTests
{
    private static (SystemDocument Doc, DocumentRevision Rev) Make(string number)
    {
        var doc = new SystemDocument
        {
            Id = Guid.NewGuid(), DocumentNumber = number, Title = "t", Category = "c", MinimumReadRole = "Reviewer",
            CurrentRevision = 1, CreatedUtc = TestDb.T0, CreatedBy = "admin",
        };
        var rev = new DocumentRevision
        {
            DocumentId = doc.Id, RevisionNumber = 1, Title = "t", Content = "body", ChangeDescription = "first release",
            AuthorId = "u", Author = "admin", CreatedUtc = TestDb.T0, PreviousHash = TestDb.Genesis, Hash = "h1",
        };
        return (doc, rev);
    }

    [Fact]
    public async Task AddAndQueryDocumentsAndRevisions()
    {
        using var test = new TestDb();
        await using var db = test.NewContext();
        var repo = new DocumentRepository(db);
        var (docB, revB) = Make("SOP-B");
        var (docA, revA) = Make("SOP-A");
        repo.Add(docB);
        repo.AddRevision(revB);
        repo.Add(docA);
        repo.AddRevision(revA);
        repo.AddRevision(new DocumentRevision
        {
            DocumentId = docA.Id, RevisionNumber = 2, Title = "t2", Content = "body2", ChangeDescription = "second release",
            AuthorId = "u", Author = "admin", CreatedUtc = TestDb.T0.AddDays(1), PreviousHash = "h1", Hash = "h2",
        });
        await new UnitOfWork(db).SaveChangesAsync();

        Assert.Equal(["SOP-A", "SOP-B"], (await repo.ListAsync()).Select(d => d.DocumentNumber));
        Assert.True(await repo.NumberExistsAsync("SOP-A"));
        Assert.False(await repo.NumberExistsAsync("SOP-Z"));
        Assert.NotNull(await repo.GetAsync(docA.Id));
        Assert.Null(await repo.GetAsync(Guid.NewGuid()));
        Assert.Equal([1, 2], (await repo.GetRevisionsAsync(docA.Id)).Select(r => r.RevisionNumber));
        Assert.Equal(2, (await repo.GetLatestRevisionAsync(docA.Id))!.RevisionNumber);
        Assert.Null(await repo.GetLatestRevisionAsync(Guid.NewGuid()));
    }
}

public class UnitOfWorkTests
{
    [Fact]
    public async Task ConcurrentUpdate_ThrowsConcurrencyConflict()
    {
        using var test = new TestDb();
        var record = TestDb.Record();
        await using (var seed = test.NewContext())
        {
            seed.Records.Add(record);
            await seed.SaveChangesAsync();
        }

        await using var first = test.NewContext();
        await using var second = test.NewContext();
        var r1 = await first.Records.SingleAsync();
        var r2 = await second.Records.SingleAsync();
        r1.Title = "first writer";
        r1.Version++;
        await new UnitOfWork(first).SaveChangesAsync();
        r2.Title = "second writer";
        r2.Version++;

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => new UnitOfWork(second).SaveChangesAsync());
    }

    [Fact]
    public async Task ForkedChain_DuplicateSequenceIsRejectedAsConflict()
    {
        using var test = new TestDb();
        var id = Guid.NewGuid();
        await using (var seed = test.NewContext())
        {
            seed.AuditTrail.Add(TestDb.Entry(id, 1, TestDb.Genesis));
            await seed.SaveChangesAsync();
        }

        await using var db = test.NewContext();
        db.AuditTrail.Add(TestDb.Entry(id, 1, TestDb.Genesis));

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => new UnitOfWork(db).SaveChangesAsync());
    }

    [Fact]
    public async Task OtherDatabaseErrors_AreNotSwallowed()
    {
        using var test = new TestDb();
        await using var db = test.NewContext();
        db.Records.Add(new ElectronicRecord { Id = Guid.NewGuid(), RecordType = null! });

        await Assert.ThrowsAsync<DbUpdateException>(() => new UnitOfWork(db).SaveChangesAsync());
    }

    [Fact]
    public void IsUniqueViolation_RecognizesPostgresAndSqliteAndIgnoresOthers()
    {
        Assert.True(UnitOfWork.IsUniqueViolation(new DbUpdateException("x", new FakeProviderException { SqlState = "23505" })));
        Assert.True(UnitOfWork.IsUniqueViolation(new DbUpdateException("x", new Exception("outer", new FakeProviderException { SqliteExtendedErrorCode = 2067 }))));
        Assert.False(UnitOfWork.IsUniqueViolation(new DbUpdateException("x", new FakeProviderException { SqlState = "23503", SqliteExtendedErrorCode = 1299 })));
        Assert.False(UnitOfWork.IsUniqueViolation(new DbUpdateException("x")));
    }

    private sealed class FakeProviderException : Exception
    {
        public string? SqlState { get; init; }

        public int SqliteExtendedErrorCode { get; init; }
    }
}
