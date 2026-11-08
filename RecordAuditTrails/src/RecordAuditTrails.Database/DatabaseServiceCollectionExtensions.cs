using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RecordAuditTrails.Database;

public static class DatabaseServiceCollectionExtensions
{
    public const string ConnectionStringName = "AuditDb";

    /// <summary>Registers the PostgreSQL context, the immutability guard and the repositories.</summary>
    public static IServiceCollection AddAuditDatabase(this IServiceCollection services, string connectionString) =>
        services.AddAuditDatabase(_ => connectionString);

    /// <summary>As above, but resolves the connection string when the context is first created, after all configuration sources are loaded.</summary>
    public static IServiceCollection AddAuditDatabase(this IServiceCollection services, Func<IServiceProvider, string?> connectionString)
    {
        services.AddSingleton<ImmutabilityInterceptor>();
        services.AddDbContext<AuditDbContext>((sp, options) =>
        {
            var cs = connectionString(sp);
            if (string.IsNullOrWhiteSpace(cs))
            {
                throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");
            }
            options.UseNpgsql(cs, npgsql => npgsql.EnableRetryOnFailure(3))
                .AddInterceptors(sp.GetRequiredService<ImmutabilityInterceptor>());
        });
        return services.AddAuditRepositories();
    }

    /// <summary>Registers repositories only; used with any provider already configured for <see cref="AuditDbContext"/>.</summary>
    public static IServiceCollection AddAuditRepositories(this IServiceCollection services)
    {
        services.AddScoped<IRecordRepository, RecordRepository>();
        services.AddScoped<IAuditTrailRepository, AuditTrailRepository>();
        services.AddScoped<IAccessLogRepository, AccessLogRepository>();
        services.AddScoped<IDocumentRepository, DocumentRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        return services;
    }

    public static async Task MigrateAuditDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("RecordAuditTrails.Database")
            .LogInformation("Applying database migrations.");
        await db.Database.MigrateAsync(ct);
    }
}
