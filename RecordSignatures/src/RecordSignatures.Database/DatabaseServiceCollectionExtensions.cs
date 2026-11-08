using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RecordSignatures.Core.Abstractions;

namespace RecordSignatures.Database;

public static class DatabaseServiceCollectionExtensions
{
    public const string ConnectionStringName = "SignaturesDb";

    /// <summary>Registers the PostgreSQL context, the immutability guard and the stores.</summary>
    public static IServiceCollection AddSignaturesDatabase(this IServiceCollection services, string connectionString) =>
        services.AddSignaturesDatabase(_ => connectionString);

    /// <summary>As above, but resolves the connection string when the context is first created, after all configuration sources are loaded.</summary>
    public static IServiceCollection AddSignaturesDatabase(this IServiceCollection services, Func<IServiceProvider, string?> connectionString)
    {
        services.AddSingleton<ImmutabilityInterceptor>();
        services.AddDbContext<SignaturesDbContext>((sp, options) =>
        {
            var cs = connectionString(sp);
            if (string.IsNullOrWhiteSpace(cs))
            {
                throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");
            }
            // No retry-on-failure strategy: services use explicit transactions around calls to RecordAuditTrails.
            options.UseNpgsql(cs).AddInterceptors(sp.GetRequiredService<ImmutabilityInterceptor>());
        });
        return services.AddSignatureStores();
    }

    /// <summary>Registers the stores only; used with any provider already configured for <see cref="SignaturesDbContext"/>.</summary>
    public static IServiceCollection AddSignatureStores(this IServiceCollection services)
    {
        services.AddScoped<ISignerStore, SignerStore>();
        services.AddScoped<ISignatureStore, SignatureStore>();
        services.AddScoped<ISecurityStore, SecurityStore>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        return services;
    }

    public static async Task MigrateSignaturesDatabaseAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SignaturesDbContext>();
        scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("RecordSignatures.Database")
            .LogInformation("Applying database migrations.");
        await db.Database.MigrateAsync(ct);
    }
}
