using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RecordSignatures.Core.Abstractions;
using RecordSignatures.Core.Integration;
using RecordSignatures.Core.Models;
using RecordSignatures.Core.Reporting;
using RecordSignatures.Core.Sealing;
using RecordSignatures.Core.Services;

namespace RecordSignatures.Core.Tests;

public class ReportingTests
{
    private readonly Harness _h = new();

    [Theory]
    [InlineData("json", true)]
    [InlineData("TEXT", true)]
    [InlineData("html", true)]
    [InlineData("pdf", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("42", false)]
    public void TryParseFormat_AcceptsOnlyKnownFormats(string? input, bool ok) =>
        Assert.Equal(ok, SignatureReportExporter.TryParseFormat(input, out _));

    private async Task<Guid> SignedRecordAsync()
    {
        await _h.ActivateAsync(_h.Tech, "Tina <Tech> & Co");
        var record = _h.Audit.AddRecord(title: "Batch <b>7</b>");
        await _h.Signatures.SignAsync(_h.Sign(record, comment: "</td><script>alert(1)</script>"), _h.Tech);
        return record;
    }

    [Fact]
    public async Task Json_IsValid_AndCarriesEverythingPart1150Requires()
    {
        var record = await SignedRecordAsync();
        var report = await _h.Reports.ExportAsync(record, ReportFormat.Json, _h.Reviewer);
        using var doc = JsonDocument.Parse(report.Content);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("isIntact").GetBoolean());
        Assert.Equal("rob.reviewer", root.GetProperty("generatedBy").GetString());
        var sig = root.GetProperty("signatures")[0];
        Assert.Equal("Tina <Tech> & Co", sig.GetProperty("printedName").GetString());
        Assert.Equal("2026-03-01T12:00:00.000000Z", sig.GetProperty("signedUtc").GetString());
        Assert.Equal("Authorship", sig.GetProperty("meaning").GetString());
        Assert.Equal("Valid", sig.GetProperty("status").GetString());
        Assert.Equal(1, sig.GetProperty("signedRecordVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, sig.GetProperty("revokedBy").ValueKind);
    }

    [Fact]
    public async Task Html_EncodesEveryUserSuppliedValue()
    {
        var record = await SignedRecordAsync();
        var html = (await _h.Reports.ExportAsync(record, ReportFormat.Html, _h.Reviewer)).Content;
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>7</b>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Tina &lt;Tech&gt; &amp; Co", html);
        Assert.Contains("All signatures are intact.", html);
    }

    [Fact]
    public async Task Text_ListsTheSignatureBlock()
    {
        var record = await SignedRecordAsync();
        var text = (await _h.Reports.ExportAsync(record, ReportFormat.Text, _h.Reviewer)).Content;
        Assert.Contains("Signed by    : Tina <Tech> & Co (tina.tech)", text);
        Assert.Contains("Date/time    : 2026-03-01T12:00:00.000000Z (UTC)", text);
        Assert.Contains("Meaning      : Authorship - ", text);
        Assert.Contains("all signatures intact", text);
        Assert.Contains("Comment", text);
    }

    [Fact]
    public async Task UnsignedRecord_SaysSo_InEveryFormat()
    {
        var record = _h.Audit.AddRecord();
        Assert.Contains("has not been signed", (await _h.Reports.ExportAsync(record, ReportFormat.Text, _h.Reviewer)).Content);
        Assert.Contains("has not been signed", (await _h.Reports.ExportAsync(record, ReportFormat.Html, _h.Reviewer)).Content);
        using var doc = JsonDocument.Parse((await _h.Reports.ExportAsync(record, ReportFormat.Json, _h.Reviewer)).Content);
        Assert.Equal(0, doc.RootElement.GetProperty("signatures").GetArrayLength());
    }

    [Fact]
    public async Task ProblemsAndRevocationsAndDeletedRecords_AreShown()
    {
        await _h.ActivateAsync(_h.Tech);
        await _h.ActivateAsync(_h.Manager);
        var record = _h.Audit.AddRecord();
        var first = await _h.Signatures.SignAsync(_h.Sign(record), _h.Tech);
        await _h.Signatures.SignAsync(_h.Sign(record, 1, MeaningCatalog.Approval, _h.Manager), _h.Manager);
        await _h.Signatures.RevokeAsync(first.Id, new RevokeSignatureRequest("Signed in error"), _h.Manager);
        _h.Data.Signatures[1] = _h.Data.Signatures[1] with { Comment = "edited in the database" };
        _h.Audit.Records[record].IsDeleted = true;

        var text = (await _h.Reports.ExportAsync(record, ReportFormat.Text, _h.Reviewer)).Content;
        Assert.Contains("(deleted)", text);
        Assert.Contains("PROBLEMS FOUND", text);
        Assert.Contains("[Revoked]", text);
        Assert.Contains("Note         : Revoked: Signed in error", text);

        var html = (await _h.Reports.ExportAsync(record, ReportFormat.Html, _h.Reviewer)).Content;
        Assert.Contains("Problems found", html);
        Assert.Contains("(deleted)", html);

        using var doc = JsonDocument.Parse((await _h.Reports.ExportAsync(record, ReportFormat.Json, _h.Reviewer)).Content);
        var sigs = doc.RootElement.GetProperty("signatures");
        Assert.Equal("mia.manager", sigs[0].GetProperty("revokedBy").GetString());
        Assert.False(doc.RootElement.GetProperty("isIntact").GetBoolean());
    }

    [Fact]
    public async Task Digest_ChangesWhenTheContentChanges_AndMatchesTheContent()
    {
        var record = _h.Audit.AddRecord();
        var a = await _h.Reports.ExportAsync(record, ReportFormat.Text, _h.Reviewer);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(a.Content))), a.Sha256);
        _h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.NotEqual(a.Sha256, (await _h.Reports.ExportAsync(record, ReportFormat.Text, _h.Reviewer)).Sha256);
    }
}

public class WiringTests
{
    private static ServiceProvider Build(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Signatures:ActiveSealKeyId"] = "k1",
            ["Signatures:SealKeys:k1"] = TestOptions.Key1,
            ["Signatures:AuthServiceBaseUrl"] = "http://auth.test",
            ["Signatures:AuditTrailBaseUrl"] = "http://audit.test",
            ["Signatures:HttpTimeout"] = "00:00:07",
            ["Signatures:MaxFailedAttempts"] = "5",
        };
        foreach (var (k, v) in extra ?? [])
        {
            settings[k] = v;
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRecordSignatures(config);
        // Stand-ins for what the Database project registers.
        services.AddScoped<ISignerStore, InMemorySignerStore>().AddScoped<ISignatureStore, InMemorySignatureStore>()
            .AddScoped<ISecurityStore, InMemorySecurityStore>().AddScoped<IUnitOfWork, InMemoryUnitOfWork>().AddSingleton<InMemoryData>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void AllServicesResolve()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        Assert.NotNull(sp.GetRequiredService<ISignerService>());
        Assert.NotNull(sp.GetRequiredService<ISignatureService>());
        Assert.NotNull(sp.GetRequiredService<ISignatureReportService>());
        Assert.NotNull(sp.GetRequiredService<ISecurityAlertService>());
        Assert.NotNull(sp.GetRequiredService<ICredentialChallenge>());
        Assert.IsType<AuthenticationCoreClient>(sp.GetRequiredService<ICredentialVerifier>());
        Assert.IsType<AuthenticationCoreClient>(sp.GetRequiredService<ISessionRevoker>());
        Assert.IsType<AuditTrailClient>(sp.GetRequiredService<IAuditTrailClient>());
        Assert.Same(provider.GetRequiredService<SignatureSealer>(), provider.GetRequiredService<SignatureSealer>());
    }

    [Fact]
    public void OptionsAreBoundFromConfiguration()
    {
        using var provider = Build();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SignatureOptions>>().Value;
        Assert.Equal(5, options.MaxFailedAttempts);
        Assert.Equal(TimeSpan.FromSeconds(7), options.HttpTimeout);
        Assert.Equal("k1", options.ActiveSealKeyId);
    }

    [Fact]
    public void ThreeAlertSinksAreRegistered()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();
        var types = scope.ServiceProvider.GetServices<ISecurityAlertSink>().Select(s => s.GetType()).ToList();
        Assert.Contains(typeof(LoggingAlertSink), types);
        Assert.Contains(typeof(AuditTrailAlertSink), types);
        Assert.Contains(typeof(WebhookAlertSink), types);
    }

    [Fact]
    public void HttpClientsUseTheConfiguredTimeout()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        Assert.Equal(TimeSpan.FromSeconds(7), factory.CreateClient(nameof(AuthenticationCoreClient)).Timeout);
    }

    [Fact]
    public void MissingSealKeyFailsWhenTheSealerIsCreated()
    {
        using var provider = Build(new() { ["Signatures:ActiveSealKeyId"] = "" });
        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<SignatureSealer>());
    }
}
