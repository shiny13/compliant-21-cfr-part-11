using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RecordSignatures.Core.Exceptions;
using RecordSignatures.Core.Models;
using RecordSignatures.Core.Sealing;
using RecordSignatures.Database.Entities;

namespace RecordSignatures.Database.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public const string Variable = "SIGNATURES_TEST_PG";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to a PostgreSQL connection string (e.g. from docker compose) to run.";
        }
    }
}

/// <summary>Runs the real migrations on a throw-away PostgreSQL database and proves the triggers and indexes hold.</summary>
public sealed class PostgresIntegrationTests : IAsyncLifetime
{
    private string _admin = "";
    private string _name = "";
    private string _cs = "";

    public async Task InitializeAsync()
    {
        var admin = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable);
        if (string.IsNullOrWhiteSpace(admin))
        {
            return;
        }
        _admin = admin;
        _name = "sig_db_" + Guid.NewGuid().ToString("N");
        await using var conn = new NpgsqlConnection(_admin);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {_name}", conn);
        await cmd.ExecuteNonQueryAsync();
        _cs = new NpgsqlConnectionStringBuilder(_admin) { Database = _name }.ConnectionString;

        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_name == "")
        {
            return;
        }
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(_admin);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>No application interceptor: these tests prove the database itself refuses tampering.</summary>
    private SignaturesDbContext NewContext(bool withGuard = false)
    {
        var builder = new DbContextOptionsBuilder<SignaturesDbContext>().UseNpgsql(_cs);
        if (withGuard)
        {
            builder.AddInterceptors(new ImmutabilityInterceptor());
        }
        return new SignaturesDbContext(builder.Options);
    }

    private async Task SeedAsync()
    {
        await using var db = NewContext();
        var signature = TestDb.NewSignature();
        await new SignerStore(db).AddAsync(TestDb.NewSigner());
        await new SignatureStore(db).AddAsync(signature);
        await new SignatureStore(db).AddRevocationAsync(new SignatureRevocation(signature.Id, TestDb.T0, "m", "mia", "mistake"));
        await new SecurityStore(db).AddAttemptAsync(new SigningAttempt(Guid.NewGuid(), "u1", "a", TestDb.T0, AttemptOutcome.Success, null));
        await new SecurityStore(db).AddAlertAsync(new SecurityAlert(Guid.NewGuid(), TestDb.T0, AlertSeverity.Info, AlertType.SignerLockedOut, "u1", "a", "d", null));
    }

    private async Task<PostgresException> SqlFails(string sql)
    {
        await using var db = NewContext();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(sql));
        return Assert.IsType<PostgresException>(ex is PostgresException ? ex : ex.InnerException);
    }

    [PostgresFact]
    public async Task Migrations_CreateAllTables()
    {
        await using var db = NewContext();
        await SeedAsync();
        Assert.Equal(1, await db.Signers.CountAsync());
        Assert.Equal(1, await db.Signatures.CountAsync());
        Assert.Equal(1, await db.SignatureRevocations.CountAsync());
        Assert.Equal(1, await db.SigningAttempts.CountAsync());
        Assert.Equal(1, await db.SecurityAlerts.CountAsync());
    }

    [PostgresFact]
    public async Task Triggers_BlockUpdateDeleteAndTruncate_OnEveryAppendOnlyTable()
    {
        await SeedAsync();
        var updates = new Dictionary<string, string>
        {
            ["signatures"] = "\"Comment\" = 'tampered'",
            ["signature_revocations"] = "\"Reason\" = 'tampered'",
            ["signing_attempts"] = "\"Outcome\" = 'BadCredentials'",
            ["security_alerts"] = "\"Detail\" = 'tampered'",
        };
        foreach (var (table, set) in updates)
        {
            var update = await SqlFails($"UPDATE {table} SET {set}");
            Assert.Equal("23000", update.SqlState);
            Assert.Contains("append-only", update.MessageText);
            Assert.Equal("23000", (await SqlFails($"DELETE FROM {table}")).SqlState);
            Assert.Equal("23000", (await SqlFails($"TRUNCATE {table}")).SqlState);
        }
    }

    [PostgresFact]
    public async Task Triggers_NeverAllowASignerToBeDeleted_EvenWhenNoRowMatches()
    {
        Assert.Equal("23000", (await SqlFails("DELETE FROM signers WHERE false")).SqlState); // empty table, statement trigger still fires
        await SeedAsync();
        Assert.Equal("23000", (await SqlFails("DELETE FROM signers")).SqlState);
        Assert.Equal("23000", (await SqlFails("TRUNCATE signers")).SqlState);
    }

    [PostgresFact]
    public async Task Triggers_NeverAllowASignersIdentityToBeReassigned()
    {
        await SeedAsync();
        foreach (var column in new[] { "UserId", "Username", "EmployeeId", "EnrolledByUserId", "EnrolledByUsername" })
        {
            var ex = await SqlFails($"UPDATE signers SET \"{column}\" = 'reassigned'");
            Assert.Equal("23000", ex.SqlState);
            Assert.Contains("cannot be changed or reassigned", ex.MessageText);
        }
        Assert.Equal("23000", (await SqlFails("UPDATE signers SET \"EnrolledUtc\" = now()")).SqlState);

        await using var db = NewContext();
        Assert.Equal(1, await db.Database.ExecuteSqlRawAsync("UPDATE signers SET \"Status\" = 'Suspended', \"StatusReason\" = 'badge lost'"));
    }

    [PostgresFact]
    public async Task SignatureSeals_SurviveThePostgresMicrosecondRoundTrip()
    {
        var sealer = new SignatureSealer(TestOptions.Wrap());
        var signedUtc = TimestampPrecision.ToStorage(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567));
        var unsealed = TestDb.NewSignature() with { SignedUtc = signedUtc, SealKeyId = sealer.ActiveKeyId, SealHash = "" };
        var signature = unsealed with { SealHash = sealer.Compute(unsealed) };

        await using (var db = NewContext())
        {
            await new SignatureStore(db).AddAsync(signature);
        }
        var loaded = await new SignatureStore(NewContext()).GetAsync(signature.Id);
        Assert.Equal(signedUtc, loaded!.SignedUtc);
        Assert.True(sealer.Verify(loaded));
    }

    [PostgresFact]
    public async Task UniqueViolations_BecomeConflicts()
    {
        await using var db = NewContext();
        await new SignerStore(db).AddAsync(TestDb.NewSigner("u1", "a", "E1"));
        await Assert.ThrowsAsync<ConflictException>(() => new SignerStore(NewContext()).AddAsync(TestDb.NewSigner("u1", "b", "E2")));
        await Assert.ThrowsAsync<ConflictException>(() => new SignerStore(NewContext()).AddAsync(TestDb.NewSigner("u2", "a", "E3")));
        await Assert.ThrowsAsync<ConflictException>(() => new SignerStore(NewContext()).AddAsync(TestDb.NewSigner("u3", "c", "E1")));

        var record = Guid.NewGuid();
        await new SignatureStore(NewContext()).AddAsync(TestDb.NewSignature(record, 1));
        await Assert.ThrowsAsync<ConflictException>(() => new SignatureStore(NewContext()).AddAsync(TestDb.NewSignature(record, 1)));
    }

    [PostgresFact]
    public async Task EmployeeId_CanBeReusedOnlyAfterRevocation()
    {
        await new SignerStore(NewContext()).AddAsync(TestDb.NewSigner("u1", "a", "E1"));
        await using var db = NewContext();
        var store = new SignerStore(db);
        var first = (await store.FindByUserIdAsync("u1"))!;
        first.Revoke("left company", TestDb.T0);
        await store.UpdateAsync(first);
        await new SignerStore(NewContext()).AddAsync(TestDb.NewSigner("u2", "b", "E1"));
        await Assert.ThrowsAsync<ConflictException>(() => new SignerStore(NewContext()).AddAsync(TestDb.NewSigner("u3", "c", "E1")));
    }

    [PostgresFact]
    public async Task TwoSignersRacingForTheSameChainPosition_OnlyOneWins()
    {
        var record = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            try
            {
                await new SignatureStore(NewContext()).AddAsync(TestDb.NewSignature(record, 1));
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }));
        Assert.Equal(1, results.Count(r => r));
    }

    [PostgresFact]
    public async Task StaleSignerUpdates_AreRejected()
    {
        await new SignerStore(NewContext()).AddAsync(TestDb.NewSigner());
        var a = (await new SignerStore(NewContext()).FindByUserIdAsync("u1"))!;
        var b = (await new SignerStore(NewContext()).FindByUserIdAsync("u1"))!;
        a.Suspend("first", TestDb.T0);
        await new SignerStore(NewContext()).UpdateAsync(a);
        b.Revoke("second", TestDb.T0);
        await Assert.ThrowsAsync<ConflictException>(() => new SignerStore(NewContext()).UpdateAsync(b));
    }

    [PostgresFact]
    public async Task TheApplicationGuard_AlsoWorksOnPostgres()
    {
        await SeedAsync();
        await using var db = NewContext(withGuard: true);
        (await db.Signatures.FirstAsync()).Comment = "edited";
        await Assert.ThrowsAsync<ConflictException>(() => db.SaveChangesAsync());
    }

    [PostgresFact]
    public async Task MigrateSignaturesDatabaseAsync_AppliesMigrations_AndIsRepeatable()
    {
        var name = "sig_db_" + Guid.NewGuid().ToString("N");
        await using (var conn = new NpgsqlConnection(_admin))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        try
        {
            var cs = new NpgsqlConnectionStringBuilder(_admin) { Database = name }.ConnectionString;
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddLogging();
            services.AddSignaturesDatabase(cs);
            await using var provider = services.BuildServiceProvider();
            await provider.MigrateSignaturesDatabaseAsync();
            await provider.MigrateSignaturesDatabaseAsync();

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SignaturesDbContext>();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var conn = new NpgsqlConnection(_admin);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
