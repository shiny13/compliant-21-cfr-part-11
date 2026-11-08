using Microsoft.EntityFrameworkCore;
using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Models;
using RecordSignatures.Database;
using RecordSignatures.Database.Entities;

namespace RecordSignatures.Database.Tests;

public class SignerStoreTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SignerStore Store(SignaturesDbContext? ctx = null) => new(ctx ?? _db.NewContext());

    [Fact]
    public async Task AddedSigner_RoundTripsEveryField()
    {
        var signer = TestDb.NewSigner();
        await Store().AddAsync(signer);
        Assert.Equal(1, signer.Version);

        var loaded = await Store().FindByUserIdAsync("u1");
        Assert.NotNull(loaded);
        Assert.Equal(signer.Id, loaded.Id);
        Assert.Equal("Printed u1", loaded.PrintedName);
        Assert.Equal("Analyst", loaded.JobTitle);
        Assert.Equal(SignerStatus.Active, loaded.Status);
        Assert.Equal(TestDb.T0, loaded.EnrolledUtc);
        Assert.Equal(DateTimeKind.Utc, loaded.EnrolledUtc.Kind);
        Assert.Equal(IdentityVerificationMethod.HumanResourcesRecord, loaded.Identity.Method);
        Assert.Equal("HR-1", loaded.Identity.EvidenceReference);
        Assert.Equal("adam.admin", loaded.Identity.VerifiedByUsername);
        Assert.Equal("1.0", loaded.CertificationVersion);
        Assert.Equal(TestDb.T0.AddMinutes(5), loaded.CertificationAcceptedUtc);
        Assert.Equal(TestDb.T0.AddMonths(12), loaded.NextReviewDueUtc);
        Assert.Equal(1, loaded.Version);
    }

    [Fact]
    public async Task Find_ByUsernameIgnoresCase_AndMissingReturnsNull()
    {
        await Store().AddAsync(TestDb.NewSigner(username: "tina.tech"));
        Assert.NotNull(await Store().FindByUsernameAsync("TINA.Tech"));
        Assert.Null(await Store().FindByUsernameAsync("nobody"));
        Assert.Null(await Store().FindByUserIdAsync("nobody"));
    }

    [Fact]
    public async Task UserId_AndUsername_AreUniqueForever()
    {
        await Store().AddAsync(TestDb.NewSigner("u1", "tina", "E1"));
        var sameId = await Assert.ThrowsAsync<ConflictException>(() => Store().AddAsync(TestDb.NewSigner("u1", "other", "E2")));
        Assert.Contains("already enrolled", sameId.Message);
        await Assert.ThrowsAsync<ConflictException>(() => Store().AddAsync(TestDb.NewSigner("u2", "tina", "E3")));
    }

    [Fact]
    public async Task EmployeeId_IsUniqueWhileLive_ButReusableAfterRevocation()
    {
        await Store().AddAsync(TestDb.NewSigner("u1", "a", "E1"));
        Assert.True(await Store().EmployeeIdInUseAsync("E1"));
        await Assert.ThrowsAsync<ConflictException>(() => Store().AddAsync(TestDb.NewSigner("u2", "b", "E1")));

        await using var ctx = _db.NewContext();
        var store = Store(ctx);
        var first = (await store.FindByUserIdAsync("u1"))!;
        first.Revoke("left company", TestDb.T0);
        await store.UpdateAsync(first);

        Assert.False(await Store().EmployeeIdInUseAsync("E1"));
        await Store().AddAsync(TestDb.NewSigner("u2", "b", "E1"));
        Assert.True(await Store().EmployeeIdInUseAsync("E1"));
    }

    [Fact]
    public async Task Update_PersistsChanges_AndBumpsTheVersion()
    {
        await Store().AddAsync(TestDb.NewSigner());
        await using var ctx = _db.NewContext();
        var store = Store(ctx);
        var signer = (await store.FindByUserIdAsync("u1"))!;
        signer.Suspend("badge lost", TestDb.T0.AddDays(1));

        await store.UpdateAsync(signer);

        Assert.Equal(2, signer.Version);
        var loaded = (await Store().FindByUserIdAsync("u1"))!;
        Assert.Equal(SignerStatus.Suspended, loaded.Status);
        Assert.Equal("badge lost", loaded.StatusReason);
        Assert.Equal(2, loaded.Version);
    }

    [Fact]
    public async Task Update_WithAStaleVersion_IsAConflict()
    {
        await Store().AddAsync(TestDb.NewSigner());
        var a = (await Store().FindByUserIdAsync("u1"))!;
        var b = (await Store().FindByUserIdAsync("u1"))!;
        a.Suspend("first writer", TestDb.T0);
        await Store().UpdateAsync(a);

        b.Revoke("second writer", TestDb.T0);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Store().UpdateAsync(b));
        Assert.Contains("changed by someone else", ex.Message);
    }

    [Fact]
    public async Task Update_WhenAnotherWriterWinsTheRace_IsAConflict_NotALostUpdate()
    {
        await Store().AddAsync(TestDb.NewSigner());
        await using var ctx1 = _db.NewContext();
        var store1 = Store(ctx1);
        var signer = (await store1.FindByUserIdAsync("u1"))!;
        // ctx1 loads and tracks the row inside UpdateAsync; make another context win first via raw SQL.
        signer.Suspend("mine", TestDb.T0);
        ctx1.Signers.Local.Clear();
        var tracked = await ctx1.Signers.FirstAsync();
        await using (var ctx2 = _db.NewContext())
        {
            await ctx2.Database.ExecuteSqlRawAsync("UPDATE signers SET Version = Version + 1, StatusReason = 'theirs'");
        }
        tracked.StatusReason = "mine";
        await Assert.ThrowsAsync<ConflictException>(() => SaveViaHelper(ctx1));
    }

    private static Task SaveViaHelper(SignaturesDbContext ctx) =>
        (Task)typeof(SignerStore).Assembly.GetType("RecordSignatures.Database.SaveHelper")!
            .GetMethod("SaveAsync")!.Invoke(null, [ctx, "conflict", CancellationToken.None])!;

    [Fact]
    public async Task Update_OfAMissingSigner_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => Store().UpdateAsync(TestDb.NewSigner()));
    }

    [Fact]
    public async Task List_FiltersOrdersAndPages()
    {
        await Store().AddAsync(TestDb.NewSigner("u1", "a", "E1", SignerStatus.Active));
        await Store().AddAsync(TestDb.NewSigner("u2", "b", "E2", SignerStatus.Suspended));
        var overdue = TestDb.NewSigner("u3", "c", "E3", SignerStatus.Active);
        overdue.NextReviewDueUtc = TestDb.T0.AddDays(-1);
        await Store().AddAsync(overdue);
        var revokedOverdue = TestDb.NewSigner("u4", "d", "E4", SignerStatus.Revoked);
        revokedOverdue.NextReviewDueUtc = TestDb.T0.AddDays(-1);
        await Store().AddAsync(revokedOverdue);

        Assert.Equal(["u1", "u2", "u3", "u4"], (await Store().ListAsync(null, false, TestDb.T0, 0, 10)).Select(s => s.UserId));
        Assert.Equal(["u1", "u3"], (await Store().ListAsync(SignerStatus.Active, false, TestDb.T0, 0, 10)).Select(s => s.UserId));
        Assert.Equal(["u3"], (await Store().ListAsync(null, true, TestDb.T0, 0, 10)).Select(s => s.UserId));
        Assert.Equal(["u2"], (await Store().ListAsync(null, false, TestDb.T0, 1, 1)).Select(s => s.UserId));
    }
}

public class SignatureStoreTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SignatureStore Store() => new(_db.NewContext());

    [Fact]
    public async Task Signature_RoundTripsEveryField_IncludingMicrosecondTimestamps()
    {
        var s = TestDb.NewSignature();
        await Store().AddAsync(s);
        var loaded = await Store().GetAsync(s.Id);
        Assert.Equal(s, loaded);
        Assert.Equal(DateTimeKind.Utc, loaded!.SignedUtc.Kind);
        Assert.Null(await Store().GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ListAndLast_FollowTheChainOrder()
    {
        var record = Guid.NewGuid();
        await Store().AddAsync(TestDb.NewSignature(record, 2, "p"));
        await Store().AddAsync(TestDb.NewSignature(record, 1));
        await Store().AddAsync(TestDb.NewSignature(Guid.NewGuid(), 1));

        Assert.Equal([1L, 2L], (await Store().ListForRecordAsync(record)).Select(s => s.Sequence));
        Assert.Equal(2, (await Store().GetLastForRecordAsync(record))!.Sequence);
        Assert.Null(await Store().GetLastForRecordAsync(Guid.NewGuid()));
        Assert.Empty(await Store().ListForRecordAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task TwoSignaturesCannotTakeTheSameChainPosition()
    {
        var record = Guid.NewGuid();
        await Store().AddAsync(TestDb.NewSignature(record, 1));
        var ex = await Assert.ThrowsAsync<ConflictException>(() => Store().AddAsync(TestDb.NewSignature(record, 1)));
        Assert.Contains("chain", ex.Message);
    }

    [Fact]
    public async Task ASignatureCanBeRevokedOnlyOnce()
    {
        var s = TestDb.NewSignature();
        await Store().AddAsync(s);
        var revocation = new SignatureRevocation(s.Id, TestDb.T0, "m", "mia.manager", "mistake");
        await Store().AddRevocationAsync(revocation);
        await Assert.ThrowsAsync<ConflictException>(() => Store().AddRevocationAsync(revocation with { Reason = "again" }));

        var loaded = await Store().GetRevocationsAsync([s.Id, Guid.NewGuid()]);
        Assert.Equal(revocation, Assert.Single(loaded));
        Assert.Empty(await Store().GetRevocationsAsync([]));
    }
}

public class SecurityStoreTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SecurityStore Store() => new(_db.NewContext());

    [Fact]
    public async Task Attempts_AreFilteredByUserAndTime_OldestFirst()
    {
        await Store().AddAttemptAsync(new SigningAttempt(Guid.NewGuid(), "u1", "a", TestDb.T0.AddMinutes(2), AttemptOutcome.BadCredentials, null));
        await Store().AddAttemptAsync(new SigningAttempt(Guid.NewGuid(), "u1", "a", TestDb.T0, AttemptOutcome.Success, Guid.NewGuid()));
        await Store().AddAttemptAsync(new SigningAttempt(Guid.NewGuid(), "u2", "b", TestDb.T0, AttemptOutcome.Success, null));

        var all = await Store().GetAttemptsSinceAsync("u1", TestDb.T0);
        Assert.Equal([AttemptOutcome.Success, AttemptOutcome.BadCredentials], all.Select(a => a.Outcome));
        Assert.NotNull(all[0].RecordId);
        Assert.Single(await Store().GetAttemptsSinceAsync("u1", TestDb.T0.AddMinutes(1)));
    }

    [Fact]
    public async Task Alerts_AreFilteredBySeverityAndDate_NewestFirst()
    {
        SecurityAlert Alert(int minute, AlertSeverity s, string detail) =>
            new(Guid.NewGuid(), TestDb.T0.AddMinutes(minute), s, AlertType.SignerLockedOut, "u1", "tina", detail, null);
        await Store().AddAlertAsync(Alert(0, AlertSeverity.Info, "i"));
        await Store().AddAlertAsync(Alert(1, AlertSeverity.Warning, "w"));
        await Store().AddAlertAsync(Alert(2, AlertSeverity.Critical, "c"));

        Assert.Equal(["c", "w", "i"], (await Store().ListAlertsAsync(null, null, 0, 10)).Select(a => a.Detail));
        Assert.Equal(["c", "w"], (await Store().ListAlertsAsync(AlertSeverity.Warning, null, 0, 10)).Select(a => a.Detail));
        Assert.Equal(["c"], (await Store().ListAlertsAsync(null, TestDb.T0.AddMinutes(2), 0, 10)).Select(a => a.Detail));
        Assert.Equal(["w"], (await Store().ListAlertsAsync(null, null, 1, 1)).Select(a => a.Detail));
    }
}

public class UnitOfWorkTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Commit_KeepsTheWrites()
    {
        await using var ctx = _db.NewContext();
        await using (var tx = await new UnitOfWork(ctx).BeginTransactionAsync())
        {
            await new SignerStore(ctx).AddAsync(TestDb.NewSigner());
            await tx.CommitAsync();
        }
        Assert.NotNull(await new SignerStore(_db.NewContext()).FindByUserIdAsync("u1"));
    }

    [Fact]
    public async Task DisposingWithoutCommit_RollsBack()
    {
        await using var ctx = _db.NewContext();
        await using (await new UnitOfWork(ctx).BeginTransactionAsync())
        {
            await new SignerStore(ctx).AddAsync(TestDb.NewSigner());
        }
        Assert.Null(await new SignerStore(_db.NewContext()).FindByUserIdAsync("u1"));
    }
}

public class SaveHelperTests
{
    private sealed class PgLikeException : Exception
    {
        public string SqlState => "23505";
    }

    private sealed class SqliteLikeException(int code) : Exception
    {
        public int SqliteExtendedErrorCode { get; } = code;
    }

    [Fact]
    public void UniqueViolation_IsRecognisedForPostgresAndSqliteCodes()
    {
        Assert.True(SaveHelper.IsUniqueViolation(new DbUpdateException("x", new PgLikeException())));
        Assert.True(SaveHelper.IsUniqueViolation(new DbUpdateException("x", new SqliteLikeException(2067))));
        Assert.True(SaveHelper.IsUniqueViolation(new DbUpdateException("x", new SqliteLikeException(1555))));
        Assert.True(SaveHelper.IsUniqueViolation(new DbUpdateException("x", new InvalidOperationException("outer", new PgLikeException()))));
    }

    [Fact]
    public void OtherConstraintFailures_AreNotMistakenForConflicts()
    {
        Assert.False(SaveHelper.IsUniqueViolation(new DbUpdateException("x", new SqliteLikeException(1299)))); // NOT NULL
        Assert.False(SaveHelper.IsUniqueViolation(new DbUpdateException("x", new InvalidOperationException())));
        Assert.False(SaveHelper.IsUniqueViolation(new DbUpdateException("x")));
    }

    [Fact]
    public async Task ANotNullViolation_SurfacesAsTheOriginalDatabaseError()
    {
        using var db = new TestDb();
        await using var ctx = db.NewContext();
        var entity = new SignerEntity { Id = Guid.NewGuid() };
        ctx.Signers.Add(entity);
        ctx.Entry(entity).Property(e => e.UserId).CurrentValue = null!;
        await Assert.ThrowsAsync<DbUpdateException>(() => SaveHelper.SaveAsync(ctx, "conflict", CancellationToken.None));
    }
}
