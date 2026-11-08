using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using AuthenticationCore.Shared.Claims;
using AuthenticationCore.Shared.Keys;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using RecordAuditTrails.Api;
using RecordAuditTrails.Database;

namespace RecordAuditTrails.Api.Tests;

/// <summary>
/// Hosts the real API in memory. Tokens are signed with a test RSA key that the API trusts through the same
/// AuthenticationCore validation it uses in production; the database is in-memory SQLite.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<ApiMarker>
{
    public const string Issuer = "https://auth.local";
    public const string Audience = "authentication-core-api";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Dictionary<string, string?> _extraConfig;
    private readonly bool _useSqlite;

    public ApiFactory(Dictionary<string, string?>? extraConfig = null, bool useSqlite = true)
    {
        _extraConfig = extraConfig ?? [];
        _useSqlite = useSqlite;
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["AuthenticationCore:PublicKeyPem"] = _rsa.ExportSubjectPublicKeyInfoPem(),
                ["AuthenticationCore:JwksUrl"] = "",
                ["AuthenticationCore:Revocation:FeedUrl"] = "",
                ["ConnectionStrings:AuditDb"] = "Host=127.0.0.1;Port=1;Database=unused;Username=u;Password=p;Timeout=1",
            };
            foreach (var (key, value) in _extraConfig)
            {
                settings[key] = value;
            }
            config.AddInMemoryCollection(settings);
        });

        if (_useSqlite)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AuditDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<AuditDbContext>>();
                services.AddDbContext<AuditDbContext>(o => o.UseSqlite(_connection).AddInterceptors(new ImmutabilityInterceptor()));
            });
        }
    }

    public async Task InitializeDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task ExecuteSqlAsync(string sql)
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AuditDbContext>().Database.ExecuteSqlRawAsync(sql);
    }

    public string Token(
        string username = "tina",
        string role = "LabTechnician",
        string? subject = null,
        TimeSpan? lifetime = null,
        string? audience = null,
        string? issuer = null,
        bool omitUsername = false,
        bool omitRole = false,
        RSA? signingKey = null)
    {
        var key = signingKey ?? _rsa;
        var now = DateTimeOffset.UtcNow;
        var claims = new List<Claim>
        {
            new(AuthClaimTypes.Subject, subject ?? $"id-{username}"),
            new(AuthClaimTypes.TokenId, Guid.NewGuid().ToString("N")),
        };
        if (!omitUsername)
        {
            claims.Add(new(AuthClaimTypes.Username, username));
        }
        if (!omitRole)
        {
            claims.Add(new(AuthClaimTypes.Role, role));
        }
        var life = lifetime ?? TimeSpan.FromMinutes(5);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = issuer ?? Issuer,
            Audience = audience ?? Audience,
            IssuedAt = life < TimeSpan.Zero ? now.UtcDateTime + life - TimeSpan.FromMinutes(1) : now.UtcDateTime,
            NotBefore = life < TimeSpan.Zero ? now.UtcDateTime + life - TimeSpan.FromMinutes(1) : now.UtcDateTime,
            Expires = (now + life).UtcDateTime,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = KeyIdentifier.FromRsa(key) }, SecurityAlgorithms.RsaSha256),
        });
    }

    public HttpClient ClientFor(string username, string role)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(username, role));
        return client;
    }

    public HttpClient Technician() => ClientFor("tina.tech", "LabTechnician");

    public HttpClient Reviewer() => ClientFor("rob.reviewer", "Reviewer");

    public HttpClient Manager() => ClientFor("mia.manager", "LabManager");

    public HttpClient Admin() => ClientFor("adam.admin", "Administrator");

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
            _rsa.Dispose();
        }
    }
}

public sealed class ApiFixture : IAsyncLifetime
{
    public ApiFactory Factory { get; } = new();

    public async Task InitializeAsync() => await Factory.InitializeDatabaseAsync();

    public Task DisposeAsync()
    {
        Factory.Dispose();
        return Task.CompletedTask;
    }
}
