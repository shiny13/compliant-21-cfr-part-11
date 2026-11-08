using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RecordAuditTrails.Database;

/// <summary>Used only by `dotnet ef` to generate migrations.</summary>
[ExcludeFromCodeCoverage]
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql("Host=localhost;Database=record_audit_trails;Username=postgres;Password=design-time-only")
            .Options);
}
