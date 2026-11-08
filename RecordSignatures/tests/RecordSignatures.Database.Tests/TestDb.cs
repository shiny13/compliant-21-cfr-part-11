using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RecordSignatures.Core.Models;
using RecordSignatures.Core.Sealing;
using RecordSignatures.Database;

namespace RecordSignatures.Database.Tests;

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

    public SignaturesDbContext NewContext() => new(new DbContextOptionsBuilder<SignaturesDbContext>()
        .UseSqlite(_connection)
        .AddInterceptors(new ImmutabilityInterceptor())
        .Options);

    public void Dispose() => _connection.Dispose();

    public static readonly DateTime T0 = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    public static Signer NewSigner(string userId = "u1", string? username = null, string? employeeId = null, SignerStatus status = SignerStatus.Active) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Username = username ?? $"user.{userId}",
        PrintedName = $"Printed {userId}",
        EmployeeId = employeeId ?? $"E-{userId}",
        JobTitle = "Analyst",
        Status = status,
        StatusReason = "Enrolled",
        StatusChangedUtc = T0,
        EnrolledUtc = T0,
        EnrolledByUserId = "adm",
        EnrolledByUsername = "adam.admin",
        Identity = new IdentityVerification(IdentityVerificationMethod.HumanResourcesRecord, "HR-1", "adm", "adam.admin", T0),
        CertificationVersion = "1.0",
        CertificationAcceptedUtc = T0.AddMinutes(5),
        LastReviewedUtc = T0,
        NextReviewDueUtc = T0.AddMonths(12),
    };

    public static SignatureRecord NewSignature(Guid? recordId = null, long sequence = 1, string previous = CanonicalHasher.Genesis, Guid? id = null) => new(
        id ?? Guid.NewGuid(), recordId ?? Guid.NewGuid(), 1, new string('a', 64), sequence, "u1", "user.u1", "Printed u1",
        "Review", "I have reviewed this record.", "fine", T0.AddTicks(1230), previous, "k1", new string('b', 64));
}
