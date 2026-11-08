using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RecordSignatures.Database;

/// <summary>Used only by `dotnet ef` to generate migrations.</summary>
[ExcludeFromCodeCoverage]
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<SignaturesDbContext>
{
    public SignaturesDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SignaturesDbContext>()
            .UseNpgsql("Host=localhost;Database=record_signatures;Username=postgres;Password=design-time-only")
            .Options);
}
