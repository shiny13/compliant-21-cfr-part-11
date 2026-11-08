using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RecordSignatures.Core.Abstractions;
using RecordSignatures.Core.Integration;

namespace RecordSignatures.Api.Tests;

/// <summary>The real RecordAuditTrails API (SQLite) in memory, trusting the same test signing key.</summary>
public sealed class AuditTrailsHost : WebApplicationFactory<RecordAuditTrails.Api.ApiMarker>
{
    private readonly string _publicKeyPem;
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public AuditTrailsHost(string publicKeyPem)
    {
        _publicKeyPem = publicKeyPem;
        _connection.Open();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AuthenticationCore:PublicKeyPem"] = _publicKeyPem,
            ["AuthenticationCore:JwksUrl"] = "",
            ["AuthenticationCore:Revocation:FeedUrl"] = "",
            ["ConnectionStrings:AuditDb"] = "Host=127.0.0.1;Port=1;Database=unused;Username=u;Password=p;Timeout=1",
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<RecordAuditTrails.Database.AuditDbContext>>();
            services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<RecordAuditTrails.Database.AuditDbContext>>();
            services.AddDbContext<RecordAuditTrails.Database.AuditDbContext>(o =>
                o.UseSqlite(_connection).AddInterceptors(new RecordAuditTrails.Database.ImmutabilityInterceptor()));
        });
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RecordAuditTrails.Database.AuditDbContext>().Database.EnsureCreatedAsync();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}

/// <summary>
/// Signatures API -> real AuditTrailClient -> real RecordAuditTrails API. Proves every signature, enrollment and alert
/// really lands in the RecordAuditTrails audit trail, under the acting individual's own identity.
/// </summary>
public sealed class CrossServiceTests : IAsyncLifetime
{
    private readonly ApiFactory _signatures;
    private AuditTrailsHost _audit = null!;

    public CrossServiceTests()
    {
        ApiFactory? self = null;
        _signatures = self = new ApiFactory(configureServices: services =>
        {
            services.AddSingleton<ICredentialVerifier>(self!.Auth);
            services.AddSingleton<ISessionRevoker>(self.Auth);
            services.AddHttpClient<IAuditTrailClient, AuditTrailClient>()
                .ConfigurePrimaryHttpMessageHandler(() => self.AuditHandler());
        });
    }

    public async Task InitializeAsync()
    {
        _audit = new AuditTrailsHost(_signatures.PublicKeyPem);
        await _audit.InitializeAsync();
        _signatures.SetAuditHandler(() => _audit.Server.CreateHandler());
        await _signatures.InitializeDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        _signatures.Dispose();
        _audit.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient AuditClient(string username)
    {
        var client = _audit.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _signatures.Token(username));
        return client;
    }

    private async Task<Guid> CreateRealRecordAsync()
    {
        var response = await AuditClient("tina.tech").PostJsonAsync("/api/records", new { recordType = "BatchRecord", title = "Batch 7", fields = new { pH = "7.0" } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("id").GetGuid();
    }

    private async Task<System.Text.Json.JsonElement> TrailOfAsync(Guid recordId) =>
        await (await AuditClient("rob.reviewer").GetAsync($"/api/records/{recordId}/audit-trail")).JsonAsync();

    [Fact]
    public async Task ASignature_IsRecordedInTheRealAuditTrail_UnderTheSignersIdentity()
    {
        await _signatures.ActivateAsync("tina.tech", "Tina Technician");
        var record = await CreateRealRecordAsync();

        var response = await _signatures.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record, comment: "Authored by me"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var signatureId = (await response.JsonAsync()).GetProperty("id").GetGuid();

        // The signature's id is the id of its record in RecordAuditTrails, and that record carries a full audit trail.
        var entries = await TrailOfAsync(signatureId);
        Assert.All(entries.EnumerateArray(), e =>
        {
            Assert.Equal("tina.tech", e.GetProperty("username").GetString());
            Assert.Equal("Create", e.GetProperty("action").GetString());
            Assert.Contains("Electronic signature executed: Authorship", e.GetProperty("reason").GetString());
        });
        var byField = entries.EnumerateArray().ToDictionary(e => e.GetProperty("fieldName").GetString()!, e => e.GetProperty("newValue").GetString());
        Assert.Equal("Tina Technician", byField["signerPrintedName"]);
        Assert.Equal("Authorship", byField["meaning"]);
        Assert.Equal(record.ToString(), byField["signedRecordId"]);
        Assert.Equal("1", byField["signedRecordVersion"]);
        Assert.Equal("Authored by me", byField["comment"]);
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2}T", byField["signedUtc"]);

        var real = await (await AuditClient("tina.tech").GetAsync($"/api/records/{signatureId}")).JsonAsync();
        Assert.Equal("ElectronicSignature", real.GetProperty("recordType").GetString());
    }

    [Fact]
    public async Task Enrollment_Certification_AndSuspension_AreAllAuditedWithTheirReasons()
    {
        await _signatures.ActivateAsync("tina.tech");
        await _signatures.Admin().PostJsonAsync("/api/signers/id-tina.tech/suspend", new { reason = "Password may be compromised", revokeSessions = false });

        var signer = await (await _signatures.Manager().GetAsync("/api/signers/id-tina.tech")).JsonAsync();
        var entries = await TrailOfAsync(signer.GetProperty("id").GetGuid());
        var statusChanges = entries.EnumerateArray().Where(e => e.GetProperty("fieldName").GetString() == "status").ToList();
        Assert.Equal(["PendingCertification", "Active", "Suspended"], statusChanges.Select(e => e.GetProperty("newValue").GetString()));
        Assert.Equal(["adam.admin", "tina.tech", "adam.admin"], statusChanges.Select(e => e.GetProperty("username").GetString()));
        Assert.Equal("Active", statusChanges[2].GetProperty("oldValue").GetString());
        Assert.Contains("Password may be compromised", statusChanges[2].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task EditingTheRealRecordAfterSigning_ShowsOnTheSignature()
    {
        await _signatures.ActivateAsync("tina.tech");
        var record = await CreateRealRecordAsync();
        await _signatures.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record));

        var clean = await (await _signatures.Technician().GetAsync($"/api/records/{record}/signatures")).JsonAsync();
        Assert.Equal("Valid", clean.GetProperty("signatures")[0].GetProperty("status").GetString());

        var edit = await AuditClient("tina.tech").SendJsonAsync(HttpMethod.Put, $"/api/records/{record}",
            new { fields = new { pH = "6.5" }, reason = "Corrected the pH reading", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        var changed = await (await _signatures.Technician().GetAsync($"/api/records/{record}/signatures")).JsonAsync();
        Assert.Equal("RecordModifiedSinceSigning", changed.GetProperty("signatures")[0].GetProperty("status").GetString());
        Assert.Equal(2, changed.GetProperty("record").GetProperty("version").GetInt32());
        Assert.Equal("6.5", changed.GetProperty("record").GetProperty("fields").GetProperty("pH").GetString());

        // Signing the stale version is refused; the new version can be signed.
        Assert.Equal(HttpStatusCode.Conflict, (await _signatures.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record, version: 1, meaning: "Responsibility"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _signatures.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record, version: 2, meaning: "Responsibility"))).StatusCode);
    }

    [Fact]
    public async Task RevokingASignature_IsAModifyEntryWithTheOldAndNewStatus()
    {
        await _signatures.ActivateAsync("tina.tech");
        var record = await CreateRealRecordAsync();
        var id = (await (await _signatures.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record))).JsonAsync()).GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await _signatures.Manager().PostJsonAsync($"/api/signatures/{id}/revoke", new { reason = "Signed the wrong batch" })).StatusCode);

        var entries = await TrailOfAsync(id);
        var status = entries.EnumerateArray().Single(e => e.GetProperty("fieldName").GetString() == "status" && e.GetProperty("action").GetString() == "Modify");
        Assert.Equal("Executed", status.GetProperty("oldValue").GetString());
        Assert.Equal("Revoked", status.GetProperty("newValue").GetString());
        Assert.Equal("mia.manager", status.GetProperty("username").GetString());
        Assert.Contains("Signed the wrong batch", status.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task SecurityAlerts_AreRecordedInTheRealAuditTrail()
    {
        await _signatures.ActivateAsync("tina.tech");
        var record = await CreateRealRecordAsync();
        await _signatures.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record, password: "wrong"));

        var alerts = await (await AuditClient("rob.reviewer").GetAsync("/api/records?recordType=SecurityAlert")).JsonAsync();
        Assert.Equal(1, alerts.GetArrayLength());
        Assert.Equal("FailedSignatureAuthentication", alerts[0].GetProperty("fields").GetProperty("type").GetString());
    }

    [Fact]
    public async Task TheRealAuditTrail_StaysIntact_AfterAllThat()
    {
        await _signatures.ActivateAsync("tina.tech");
        await _signatures.ActivateAsync("mia.manager");
        var record = await CreateRealRecordAsync();
        await _signatures.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record));
        await _signatures.Manager().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record, meaning: "Approval", username: "mia.manager"));

        var verify = await (await AuditClient("mia.manager").GetAsync("/api/audit-trail/verify")).JsonAsync();
        Assert.True(verify.GetProperty("isValid").GetBoolean());
    }

    [Fact]
    public async Task ErrorsFromTheRealAuditService_ArePassedOnFaithfully()
    {
        await _signatures.ActivateAsync("tina.tech");
        Assert.Equal(HttpStatusCode.NotFound, (await _signatures.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(Guid.NewGuid()))).StatusCode);

        var record = await CreateRealRecordAsync();
        var deleted = await AuditClient("tina.tech").SendJsonAsync(HttpMethod.Delete, $"/api/records/{record}", new { reason = "Entered in error" });
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var response = await _signatures.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record, version: 2));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("deleted", (await response.JsonAsync()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ATokenTheAuditServiceDoesNotTrust_IsTurnedIntoA403()
    {
        // The signer's request is authenticated here, but the forwarded token is rejected downstream.
        using var other = new AuditTrailsHost(System.Security.Cryptography.RSA.Create(2048).ExportSubjectPublicKeyInfoPem());
        await other.InitializeAsync();
        using var factory = new ApiFactory(configureServices: services =>
        {
            services.AddSingleton<ICredentialVerifier>(new FakeCredentialVerifier());
            services.AddHttpClient<IAuditTrailClient, AuditTrailClient>().ConfigurePrimaryHttpMessageHandler(() => other.Server.CreateHandler());
        });
        await factory.InitializeDatabaseAsync();
        var response = await factory.Admin().PostJsonAsync("/api/signers", new
        {
            userId = "id-x", username = "x", printedName = "X", employeeId = "E",
            identity = new { method = "Other", evidenceReference = "e" },
        });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
