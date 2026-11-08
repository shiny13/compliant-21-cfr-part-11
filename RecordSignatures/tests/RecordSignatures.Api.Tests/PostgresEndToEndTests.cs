using System.Net;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace RecordSignatures.Api.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public const string Variable = "SIGNATURES_TEST_PG";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to a PostgreSQL connection string to run.";
        }
    }
}

public class PostgresEndToEndTests
{
    [PostgresFact]
    public async Task ApiRunsMigrations_SignsOnPostgres_AndTheDatabaseRefusesTampering()
    {
        var admin = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable)!;
        var name = "sig_api_" + Guid.NewGuid().ToString("N");
        await using (var conn = new NpgsqlConnection(admin))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var cs = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;

        try
        {
            using var factory = new ApiFactory(new() { ["ConnectionStrings:SignaturesDb"] = cs, ["Database:MigrateOnStartup"] = "true" }, useSqlite: false);
            await factory.ActivateAsync("tina.tech", "Tina Technician");
            await factory.ActivateAsync("mia.manager", "Mia Manager");
            var record = factory.Audit.AddRecord();

            var first = await factory.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record));
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            var second = await factory.Manager().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record, meaning: "Approval", username: "mia.manager"));
            Assert.Equal(2, (await second.JsonAsync()).GetProperty("sequence").GetInt64());

            // Seals computed before the write still verify after PostgreSQL stored (and truncated) the timestamps.
            var result = await (await factory.Technician().GetAsync($"/api/records/{record}/signatures")).JsonAsync();
            Assert.True(result.GetProperty("isIntact").GetBoolean());
            Assert.All(result.GetProperty("signatures").EnumerateArray(), s => Assert.Equal("Valid", s.GetProperty("status").GetString()));

            Assert.Equal(HttpStatusCode.OK, (await factory.Reviewer().GetAsync($"/api/records/{record}/signatures/report?format=html")).StatusCode);

            // Even the application's own database role cannot rewrite or remove a signature.
            await Assert.ThrowsAnyAsync<Exception>(() => factory.WithDbAsync(db =>
                db.Database.ExecuteSqlRawAsync("UPDATE signatures SET \"SignerPrintedName\" = 'Mallory'")));
            await Assert.ThrowsAnyAsync<Exception>(() => factory.WithDbAsync(db =>
                db.Database.ExecuteSqlRawAsync("DELETE FROM signatures")));
            await Assert.ThrowsAnyAsync<Exception>(() => factory.WithDbAsync(db =>
                db.Database.ExecuteSqlRawAsync("DELETE FROM signers WHERE false")));

            // Duplicate enrollment is refused by the database-backed uniqueness, not just by a check.
            var dup = await factory.Admin().PostJsonAsync("/api/signers", new
            {
                userId = "id-tina.tech", username = "tina.tech", printedName = "Tina", employeeId = "Z",
                identity = new { method = "Other", evidenceReference = "e" },
            });
            Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var conn = new NpgsqlConnection(admin);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
