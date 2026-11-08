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
using RecordSignatures.Core.Abstractions;
using RecordSignatures.Database;

namespace RecordSignatures.Api.Tests;

/// <summary>
/// Hosts the real API in memory. Tokens are signed with a test RSA key that the API trusts through the same
/// AuthenticationCore validation it uses in production; the database is in-memory SQLite. AuthenticationCore's
/// password check and the RecordAuditTrails API are scriptable fakes (a real RecordAuditTrails is used in
/// <see cref="CrossServiceTests"/>).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<ApiMarker>
{
    public const string Issuer = "https://auth.local";
    public const string Audience = "authentication-core-api";
    public const string Password = "Pw-Correct-1!";

    public static readonly Dictionary<string, string> Roles = new()
    {
        ["adam.admin"] = "Administrator",
        ["mia.manager"] = "LabManager",
        ["rob.reviewer"] = "Reviewer",
        ["tina.tech"] = "LabTechnician",
        ["tom.tech"] = "LabTechnician",
    };

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly Dictionary<string, string?> _extraConfig;
    private readonly Action<IServiceCollection>? _configureServices;
    private readonly bool _useSqlite;

    private Func<HttpMessageHandler>? _auditHandler;

    public void SetAuditHandler(Func<HttpMessageHandler> handler) => _auditHandler = handler;

    public HttpMessageHandler AuditHandler() => _auditHandler!();

    public string PublicKeyPem => _rsa.ExportSubjectPublicKeyInfoPem();

    public FakeAuditTrail Audit { get; } = new();

    public FakeCredentialVerifier Auth { get; } = new();

    public ApiFactory(Dictionary<string, string?>? extraConfig = null, bool useSqlite = true, Action<IServiceCollection>? configureServices = null)
    {
        _extraConfig = extraConfig ?? [];
        _useSqlite = useSqlite;
        _configureServices = configureServices;
        _connection.Open();
        foreach (var user in Roles.Keys)
        {
            Auth.Add(user, Password, UserId(user));
        }
    }

    public static string UserId(string username) => $"id-{username}";

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
                ["ConnectionStrings:SignaturesDb"] = "Host=127.0.0.1;Port=1;Database=unused;Username=u;Password=p;Timeout=1",
                ["Signatures:ActiveSealKeyId"] = "k1",
                ["Signatures:SealKeys:k1"] = TestOptions.Key1,
                ["Signatures:AuthServiceBaseUrl"] = "http://auth.test",
                ["Signatures:AuditTrailBaseUrl"] = "http://audit.test",
                ["Signatures:OrganizationName"] = "Acme Pharma",
            };
            foreach (var (key, value) in _extraConfig)
            {
                settings[key] = value;
            }
            config.AddInMemoryCollection(settings);
        });

        builder.ConfigureServices(services =>
        {
            if (_useSqlite)
            {
                services.RemoveAll<DbContextOptions<SignaturesDbContext>>();
                services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<SignaturesDbContext>>();
                services.AddDbContext<SignaturesDbContext>(o => o.UseSqlite(_connection).AddInterceptors(new ImmutabilityInterceptor()));
            }

            if (_configureServices is null)
            {
                services.RemoveAll<ICredentialVerifier>();
                services.RemoveAll<ISessionRevoker>();
                services.RemoveAll<IAuditTrailClient>();
                services.AddSingleton<ICredentialVerifier>(Auth);
                services.AddSingleton<ISessionRevoker>(Auth);
                services.AddSingleton<IAuditTrailClient>(Audit);
            }
            else
            {
                _configureServices(services);
            }
        });
    }

    public async Task InitializeDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SignaturesDbContext>().Database.EnsureCreatedAsync();
    }

    public async Task<T> WithDbAsync<T>(Func<SignaturesDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<SignaturesDbContext>());
    }

    public string Token(
        string username = "tina.tech",
        string? role = null,
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
            new(AuthClaimTypes.Subject, subject ?? UserId(username)),
            new(AuthClaimTypes.TokenId, Guid.NewGuid().ToString("N")),
        };
        if (!omitUsername)
        {
            claims.Add(new(AuthClaimTypes.Username, username));
        }
        if (!omitRole)
        {
            claims.Add(new(AuthClaimTypes.Role, role ?? Roles.GetValueOrDefault(username, "LabTechnician")));
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

    public HttpClient ClientFor(string username, string? role = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(username, role));
        return client;
    }

    public HttpClient Technician() => ClientFor("tina.tech");

    public HttpClient Reviewer() => ClientFor("rob.reviewer");

    public HttpClient Manager() => ClientFor("mia.manager");

    public HttpClient Admin() => ClientFor("adam.admin");

    /// <summary>Enrolls the user (as the administrator) and has them accept the certification, so they can sign.</summary>
    public async Task ActivateAsync(string username, string? printedName = null)
    {
        var enroll = await Admin().PostJsonAsync("/api/signers", new
        {
            userId = UserId(username),
            username,
            printedName = printedName ?? $"Printed {username}",
            employeeId = $"E-{username}",
            jobTitle = "Analyst",
            identity = new { method = "GovernmentIssuedId", evidenceReference = "Passport sighted by HR" },
        });
        Assert.Equal(System.Net.HttpStatusCode.Created, enroll.StatusCode);
        var certify = await ClientFor(username).PostJsonAsync("/api/signers/me/certification", new { username, password = Password, statementVersion = "1.0" });
        Assert.Equal(System.Net.HttpStatusCode.OK, certify.StatusCode);
    }

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
