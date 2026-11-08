using Microsoft.EntityFrameworkCore;
using Npgsql;
using RecordAuditTrails.Core.Auditing;
using RecordAuditTrails.Core.Hashing;
using RecordAuditTrails.Models;

namespace RecordAuditTrails.Database.Tests;

/// <summary>Runs only when AUDIT_TEST_PG points at a throw-away PostgreSQL server (see README).</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string Variable = "AUDIT_TEST_PG";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to a PostgreSQL connection string to run.";
        }
    }
}

public class PostgresIntegrationTests
{
    private static async Task<(AuditDbContext Db, string ConnectionString)> NewDatabaseAsync()
    {
        var admin = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable)!;
        var name = "audit_test_" + Guid.NewGuid().ToString("N");
        await using (var conn = new NpgsqlConnection(admin))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var cs = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        var db = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>().UseNpgsql(cs).AddInterceptors(new ImmutabilityInterceptor()).Options);
        await db.Database.MigrateAsync();
        return (db, cs);
    }

    [PostgresFact]
    public async Task HashChainSurvivesPostgresRoundTrip_AndTriggersBlockTampering()
    {
        var (db, cs) = await NewDatabaseAsync();
        await using var _d = db;

        var record = TestDb.Record();
        var now = TimestampPrecision.ToStorage(DateTimeOffset.UtcNow);
        var e1 = TestDb.Entry(record.Id, 1, TestDb.Genesis, at: now);
        var e2 = TestDb.Entry(record.Id, 2, e1.Hash, at: now.AddTicks(30));
        db.Records.Add(record);
        db.AuditTrail.AddRange(e1, e2);
        db.AccessLog.Add(new AccessLogEntry { SubjectType = "Record", SubjectId = record.Id, UserId = "u", Username = "a", Role = "r", TimestampUtc = now });
        var doc = new SystemDocument { Id = Guid.NewGuid(), DocumentNumber = "D-1", Title = "t", Category = "c", MinimumReadRole = "Reviewer", CurrentRevision = 1, CreatedUtc = now, CreatedBy = "a" };
        db.Documents.Add(doc);
        db.DocumentRevisions.Add(new DocumentRevision { DocumentId = doc.Id, RevisionNumber = 1, Title = "t", Content = "c", ChangeDescription = "initial", AuthorId = "u", Author = "a", CreatedUtc = now, PreviousHash = TestDb.Genesis, Hash = "h" });
        await db.SaveChangesAsync();

        // Re-read through a fresh context: the chain must still verify after PostgreSQL's microsecond storage.
        await using var fresh = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>().UseNpgsql(cs).Options);
        var chain = await fresh.AuditTrail.Where(a => a.RecordId == record.Id).OrderBy(a => a.Sequence).ToListAsync();
        Assert.True(ChainVerifier.VerifyAuditTrail(chain).IsValid);

        // Direct SQL bypassing the application is blocked by the triggers.
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "UPDATE audit_trail SET \"NewValue\" = 'forged'",
            "DELETE FROM audit_trail",
            "TRUNCATE audit_trail",
            "UPDATE access_log SET \"Details\" = 'x'",
            "DELETE FROM document_revisions",
            "DELETE FROM records",
            "DELETE FROM system_documents",
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();
            });
            Assert.Contains("not permitted", ex.MessageText);
        }

        // Ordinary updates to the current-state record remain possible.
        await using var update = new NpgsqlCommand("UPDATE records SET \"Title\" = 'renamed'", conn);
        Assert.Equal(1, await update.ExecuteNonQueryAsync());
    }

    [PostgresFact]
    public async Task DuplicateChainPosition_IsReportedAsConflict()
    {
        var (db, _) = await NewDatabaseAsync();
        await using var _d = db;
        var id = Guid.NewGuid();
        db.AuditTrail.Add(TestDb.Entry(id, 1, TestDb.Genesis));
        await db.SaveChangesAsync();

        db.AuditTrail.Add(TestDb.Entry(id, 1, TestDb.Genesis));
        await Assert.ThrowsAsync<Core.Exceptions.ConcurrencyConflictException>(() => new UnitOfWork(db).SaveChangesAsync());
    }
}
