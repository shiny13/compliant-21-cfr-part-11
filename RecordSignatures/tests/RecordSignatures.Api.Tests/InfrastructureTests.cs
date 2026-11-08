using System.Net;
using Microsoft.Extensions.DependencyInjection;
using RecordSignatures.Core.Sealing;

namespace RecordSignatures.Api.Tests;

public class InfrastructureTests
{
    [Fact]
    public async Task Health_IsAnonymous()
    {
        using var factory = new ApiFactory();
        var response = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("healthy", (await response.JsonAsync()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Documentation_CanBeSwitchedOff()
    {
        using var factory = new ApiFactory(new() { ["Swagger:Enabled"] = "false" });
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/openapi/v1.yaml")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/index.html")).StatusCode);
    }

    [Fact]
    public void AMissingSealKey_StopsTheApiFromStarting()
    {
        using var factory = new ApiFactory(new() { ["Signatures:ActiveSealKeyId"] = "" });
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("ActiveSealKeyId", ex.ToString());
    }

    [Fact]
    public void AWeakSealKey_StopsTheApiFromStarting()
    {
        using var factory = new ApiFactory(new() { ["Signatures:SealKeys:k1"] = Convert.ToBase64String(new byte[8]) });
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("at least 32 bytes", ex.ToString());
    }

    [Fact]
    public void TheRealExternalClientsAreWiredByDefault()
    {
        using var factory = new ApiFactory(configureServices: _ => { });
        using var scope = factory.Services.CreateScope();
        Assert.IsType<RecordSignatures.Core.Integration.AuthenticationCoreClient>(scope.ServiceProvider.GetRequiredService<RecordSignatures.Core.Abstractions.ICredentialVerifier>());
        Assert.IsType<RecordSignatures.Core.Integration.AuditTrailClient>(scope.ServiceProvider.GetRequiredService<RecordSignatures.Core.Abstractions.IAuditTrailClient>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<SignatureSealer>());
    }

    [Fact]
    public async Task AnUnreachableAuditTrailService_MeansA503_NotASilentSuccess()
    {
        using var factory = new ApiFactory(new() { ["Signatures:AuditTrailBaseUrl"] = "http://127.0.0.1:1", ["Signatures:HttpTimeout"] = "00:00:02" }, configureServices: services =>
        {
            // Real clients, but identity checks are faked so only the audit trail is unreachable.
            services.AddSingleton<RecordSignatures.Core.Abstractions.ICredentialVerifier>(_ => new FakeCredentialVerifier());
        });
        await factory.InitializeDatabaseAsync();
        var response = await factory.Admin().PostJsonAsync("/api/signers", new
        {
            userId = "id-x", username = "x", printedName = "X", employeeId = "E",
            identity = new { method = "Other", evidenceReference = "e" },
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Dependency unavailable", (await response.JsonAsync()).GetProperty("title").GetString());
    }
}
