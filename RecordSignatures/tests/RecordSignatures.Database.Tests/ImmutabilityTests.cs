using Microsoft.EntityFrameworkCore;
using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Models;
using RecordSignatures.Database;
using RecordSignatures.Database.Entities;

namespace RecordSignatures.Database.Tests;

public class ImmutabilityTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<(Guid Signature, Guid Revocation, Guid Attempt, Guid Alert, Guid Signer)> SeedAsync()
    {
        await using var ctx = _db.NewContext();
        var signature = TestDb.NewSignature();
        var signer = TestDb.NewSigner();
        var revocationEntity = new SignatureRevocationEntity { Id = Guid.NewGuid(), SignatureId = signature.Id, RevokedUtc = TestDb.T0, RevokedByUserId = "m", RevokedByUsername = "mia", Reason = "mistake" };
        var attempt = new SigningAttemptEntity { Id = Guid.NewGuid(), UserId = "u1", Username = "a", AttemptedUtc = TestDb.T0, Outcome = "Success" };
        var alert = new SecurityAlertEntity { Id = Guid.NewGuid(), RaisedUtc = TestDb.T0, Severity = "Info", Type = "SignerLockedOut", Detail = "d" };
        await new SignatureStore(ctx).AddAsync(signature);
        await new SignerStore(ctx).AddAsync(signer);
        ctx.AddRange(revocationEntity, attempt, alert);
        await ctx.SaveChangesAsync();
        return (signature.Id, revocationEntity.Id, attempt.Id, alert.Id, signer.Id);
    }

    [Fact]
    public async Task AppendOnlyRows_CanBeInsertedButNeverModified()
    {
        var ids = await SeedAsync();
        await using var ctx = _db.NewContext();

        (await ctx.Signatures.FindAsync(ids.Signature))!.Comment = "edited";
        await Assert.ThrowsAsync<ConflictException>(() => ctx.SaveChangesAsync());
        ctx.ChangeTracker.Clear();

        (await ctx.SignatureRevocations.FindAsync(ids.Revocation))!.Reason = "edited";
        await Assert.ThrowsAsync<ConflictException>(() => ctx.SaveChangesAsync());
        ctx.ChangeTracker.Clear();

        (await ctx.SigningAttempts.FindAsync(ids.Attempt))!.Outcome = "BadCredentials";
        await Assert.ThrowsAsync<ConflictException>(() => ctx.SaveChangesAsync());
        ctx.ChangeTracker.Clear();

        (await ctx.SecurityAlerts.FindAsync(ids.Alert))!.Detail = "edited";
        var ex = await Assert.ThrowsAsync<ConflictException>(() => ctx.SaveChangesAsync());
        Assert.Contains("append-only", ex.Message);
        Assert.Contains("modified", ex.Message);
    }

    [Fact]
    public async Task AppendOnlyRows_CanNeverBeDeleted()
    {
        var ids = await SeedAsync();
        await using var ctx = _db.NewContext();
        ctx.Signatures.Remove((await ctx.Signatures.FindAsync(ids.Signature))!);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => ctx.SaveChangesAsync());
        Assert.Contains("deleted", ex.Message);
        ctx.ChangeTracker.Clear();

        ctx.SigningAttempts.Remove((await ctx.SigningAttempts.FindAsync(ids.Attempt))!);
        await Assert.ThrowsAsync<ConflictException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task Signers_CanNeverBeDeleted()
    {
        var ids = await SeedAsync();
        await using var ctx = _db.NewContext();
        ctx.Signers.Remove((await ctx.Signers.FindAsync(ids.Signer))!);
        var ex = await Assert.ThrowsAsync<ConflictException>(() => ctx.SaveChangesAsync());
        Assert.Contains("never reassigned", ex.Message);
    }

    [Theory]
    [InlineData(nameof(SignerEntity.UserId))]
    [InlineData(nameof(SignerEntity.Username))]
    [InlineData(nameof(SignerEntity.EmployeeId))]
    [InlineData(nameof(SignerEntity.EnrolledByUserId))]
    [InlineData(nameof(SignerEntity.EnrolledByUsername))]
    public async Task SignerIdentityColumns_CanNeverChange(string column)
    {
        var ids = await SeedAsync();
        await using var ctx = _db.NewContext();
        var entity = (await ctx.Signers.FindAsync(ids.Signer))!;
        ctx.Entry(entity).Property(column).CurrentValue = "changed";
        var ex = await Assert.ThrowsAsync<ConflictException>(() => ctx.SaveChangesAsync());
        Assert.Contains(column, ex.Message);
    }

    [Fact]
    public async Task SignerEnrollmentTime_CanNeverChange()
    {
        var ids = await SeedAsync();
        await using var ctx = _db.NewContext();
        (await ctx.Signers.FindAsync(ids.Signer))!.EnrolledUtc = TestDb.T0.AddDays(1);
        await Assert.ThrowsAsync<ConflictException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task SignerLifecycleColumns_MayChange()
    {
        var ids = await SeedAsync();
        await using var ctx = _db.NewContext();
        var entity = (await ctx.Signers.FindAsync(ids.Signer))!;
        entity.Status = "Suspended";
        entity.StatusReason = "badge lost";
        entity.PrintedName = "Tina B. Tech";
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task SynchronousSave_IsGuardedToo()
    {
        var ids = await SeedAsync();
        using var ctx = _db.NewContext();
        ctx.Signatures.Find(ids.Signature)!.Comment = "edited";
        Assert.Throws<ConflictException>(() => ctx.SaveChanges());

        ctx.ChangeTracker.Clear();
        ctx.SecurityAlerts.Add(new SecurityAlertEntity { Id = Guid.NewGuid(), RaisedUtc = TestDb.T0, Severity = "Info", Type = "SignerLockedOut", Detail = "ok" });
        Assert.Equal(1, ctx.SaveChanges());
    }

    [Fact]
    public void CheckWithoutAContext_DoesNothing() => ImmutabilityInterceptor.Check(null);
}

public class ConverterTests
{
    [Fact]
    public void UtcConverter_NormalizesLocalAndUnspecifiedKinds()
    {
        var converter = new UtcDateTimeConverter();
        var toDb = converter.ConvertToProviderExpression.Compile();
        var fromDb = converter.ConvertFromProviderExpression.Compile();

        var utc = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(utc, toDb(utc));
        Assert.Equal(DateTimeKind.Utc, toDb(utc.ToLocalTime()).Kind);
        Assert.Equal(utc, toDb(utc.ToLocalTime()));
        Assert.Equal(DateTimeKind.Utc, fromDb(new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Unspecified)).Kind);
    }
}
