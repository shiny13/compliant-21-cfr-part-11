using System.Net;
using Npgsql;

namespace RecordAuditTrails.Api.Tests;

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

public class PostgresEndToEndTests
{
    [PostgresFact]
    public async Task ApiRunsMigrationsAndPersistsAnAuditedRecordInPostgres()
    {
        var admin = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable)!;
        var name = "audit_api_" + Guid.NewGuid().ToString("N");
        await using (var conn = new NpgsqlConnection(admin))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var cs = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;

        using var factory = new ApiFactory(new() { ["ConnectionStrings:AuditDb"] = cs, ["Database:MigrateOnStartup"] = "true" }, useSqlite: false);
        var tech = factory.Technician();

        var id = await tech.CreateRecordAsync(title: "Postgres batch");
        var update = await tech.PutJsonAsync($"/api/records/{id}", new { fields = new { pH = "6.9" }, reason = "Re-measured", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var verify = await (await factory.Reviewer().GetAsync($"/api/records/{id}/audit-trail/verify")).JsonAsync();
        Assert.True(verify.GetProperty("isValid").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await factory.Reviewer().GetAsync($"/api/records/{id}/export?format=html")).StatusCode);

        var doc = await factory.Admin().PostJsonAsync("/api/documents", new { documentNumber = "PG-1", title = "t", category = "c", content = "body", minimumReadRole = "Reviewer" });
        Assert.Equal(HttpStatusCode.Created, doc.StatusCode);
        Assert.True((await (await factory.Manager().GetAsync("/api/audit-trail/verify")).JsonAsync()).GetProperty("isValid").GetBoolean());

        // A concurrent stale write is rejected by the database, not silently accepted.
        var stale = await tech.PutJsonAsync($"/api/records/{id}", new { title = "stale", reason = "stale write", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }
}

public class MigrateOnlyTests
{
    [PostgresFact]
    public async Task MigrateOnly_AppliesMigrationsAndExitsWithoutServing()
    {
        var admin = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable)!;
        var name = "audit_mig_" + Guid.NewGuid().ToString("N");
        await using (var conn = new NpgsqlConnection(admin))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var cs = new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        try
        {
            using var factory = new ApiFactory(new()
            {
                ["ConnectionStrings:AuditDb"] = cs, ["Database:MigrateOnStartup"] = "true", ["Database:MigrateOnly"] = "true",
            }, useSqlite: false);
            try
            {
                _ = factory.Services; // runs Program up to the early return
            }
            catch (InvalidOperationException)
            {
                // The test host cannot serve an application that chose to exit; the migration has already run.
            }

            await using var check = new NpgsqlConnection(cs);
            await check.OpenAsync();
            await using var query = new NpgsqlCommand("SELECT count(*) FROM \"__EFMigrationsHistory\"", check);
            Assert.Equal(2L, await query.ExecuteScalarAsync());
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
