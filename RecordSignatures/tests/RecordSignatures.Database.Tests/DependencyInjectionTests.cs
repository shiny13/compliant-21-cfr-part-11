using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RecordSignatures.Core.Abstractions;
using RecordSignatures.Database;

namespace RecordSignatures.Database.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddSignaturesDatabase_RegistersTheContextTheGuardAndTheStores()
    {
        var services = new ServiceCollection();
        services.AddSignaturesDatabase("Host=localhost;Database=x;Username=u;Password=p");
        using var provider = services.BuildServiceProvider(validateScopes: true);
        using var scope = provider.CreateScope();

        Assert.IsType<SignerStore>(scope.ServiceProvider.GetRequiredService<ISignerStore>());
        Assert.IsType<SignatureStore>(scope.ServiceProvider.GetRequiredService<ISignatureStore>());
        Assert.IsType<SecurityStore>(scope.ServiceProvider.GetRequiredService<ISecurityStore>());
        Assert.IsType<UnitOfWork>(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
        var db = scope.ServiceProvider.GetRequiredService<SignaturesDbContext>();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
        Assert.Same(provider.GetRequiredService<ImmutabilityInterceptor>(), provider.GetRequiredService<ImmutabilityInterceptor>());
    }

    [Fact]
    public void TheConnectionStringIsResolvedLazily()
    {
        var calls = 0;
        var services = new ServiceCollection();
        services.AddSignaturesDatabase(_ => { calls++; return "Host=localhost;Database=x;Username=u;Password=p"; });
        using var provider = services.BuildServiceProvider();
        Assert.Equal(0, calls);
        using var scope = provider.CreateScope();
        _ = scope.ServiceProvider.GetRequiredService<SignaturesDbContext>();
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AMissingConnectionString_FailsWithAClearMessage(string? connectionString)
    {
        var services = new ServiceCollection();
        services.AddSignaturesDatabase(_ => connectionString);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var ex = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<SignaturesDbContext>());
        Assert.Contains("SignaturesDb", ex.Message);
    }

    [Fact]
    public void AddSignatureStores_WorksWithAnyProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<SignaturesDbContext>(o => o.UseSqlite("DataSource=:memory:"));
        services.AddSignatureStores();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ISignerStore>());
    }
}
