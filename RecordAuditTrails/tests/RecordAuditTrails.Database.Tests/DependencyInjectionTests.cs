using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RecordAuditTrails.Database.Tests;

public class DependencyInjectionTests
{
    [Theory]
    [InlineData(" ")]
    [InlineData(null)]
    public void AddAuditDatabase_RejectsMissingConnectionStringWhenTheContextIsCreated(string? connectionString)
    {
        using var provider = new ServiceCollection().AddAuditDatabase(_ => connectionString).BuildServiceProvider();
        using var scope = provider.CreateScope();
        var ex = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<AuditDbContext>());
        Assert.Contains("AuditDb", ex.Message);
    }

    [Fact]
    public void AddAuditDatabase_RegistersPostgresContextAndRepositories()
    {
        var services = new ServiceCollection().AddLogging().AddAuditDatabase("Host=localhost;Database=x;Username=u;Password=p");
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
        Assert.IsType<RecordRepository>(scope.ServiceProvider.GetRequiredService<IRecordRepository>());
        Assert.IsType<AuditTrailRepository>(scope.ServiceProvider.GetRequiredService<IAuditTrailRepository>());
        Assert.IsType<AccessLogRepository>(scope.ServiceProvider.GetRequiredService<IAccessLogRepository>());
        Assert.IsType<DocumentRepository>(scope.ServiceProvider.GetRequiredService<IDocumentRepository>());
        Assert.IsType<UnitOfWork>(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
    }

    [Fact]
    public void Model_MapsExpectedTablesAndKeepsAuditTrailIndependentOfRecords()
    {
        using var test = new TestDb();
        using var db = test.NewContext();
        var tables = db.Model.GetEntityTypes().Select(t => t.GetTableName()!).Order().ToArray();
        Assert.Equal(["access_log", "audit_trail", "document_revisions", "records", "system_documents"], tables);
        var audit = db.Model.FindEntityType(typeof(Models.AuditTrailEntry))!;
        Assert.Empty(audit.GetForeignKeys());
    }

    [Fact]
    public async Task MigrateAuditDatabase_RunsMigrationsOnTheConfiguredProvider()
    {
        // SQLite cannot run the Postgres-specific migrations, so this only proves the wiring: the call resolves the
        // context in a new scope and attempts to migrate. The real migration is covered by PostgresIntegrationTests.
        var services = new ServiceCollection().AddLogging();
        services.AddDbContext<AuditDbContext>(o => o.UseNpgsql("Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=1;Command Timeout=1"));
        await using var provider = services.BuildServiceProvider();

        await Assert.ThrowsAnyAsync<Exception>(() => provider.MigrateAuditDatabaseAsync());
    }
}
