using System.Net;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using RecordSignatures.Core.Services;

namespace RecordSignatures.Api.Tests;

public class SigningFlowTests : IDisposable
{
    private readonly ApiFactory _f = new();

    public SigningFlowTests() => _f.InitializeDatabaseAsync().GetAwaiter().GetResult();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task ARecordIsSignedWithNameDateTimeAndMeaning_AndTheSigningIsAudited()
    {
        await _f.ActivateAsync("tina.tech", "Tina Technician");
        var record = _f.Audit.AddRecord(title: "Batch 7");
        var tech = _f.Technician();
        var before = DateTime.UtcNow;

        var response = await tech.PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record, comment: "I made this"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal("Tina Technician", body.GetProperty("signerPrintedName").GetString());
        Assert.Equal("Authorship", body.GetProperty("meaningCode").GetString());
        Assert.InRange(body.GetProperty("signedUtc").GetDateTime(), before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(1, body.GetProperty("recordVersion").GetInt32());
        Assert.Equal(1, body.GetProperty("sequence").GetInt64());
        Assert.Equal($"/api/signatures/{body.GetProperty("id").GetGuid()}", response.Headers.Location!.ToString());

        var audit = Assert.Single(_f.Audit.OfType(AuditFields.SignatureRecordType));
        Assert.Equal(body.GetProperty("id").GetGuid(), audit.Id);
        Assert.Equal("Tina Technician", audit.Fields["signerPrintedName"]);
        Assert.Contains(_f.Audit.OfType(AuditFields.SignerRecordType), r => r.Reasons.Any(x => x.Contains("enrolled")));
    }

    [Fact]
    public async Task SigningNeedsEnrollment_Certification_AndTheRightPassword()
    {
        var record = _f.Audit.AddRecord();
        var tech = _f.Technician();
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record))).StatusCode);

        var enroll = await _f.Admin().PostJsonAsync("/api/signers", new
        {
            userId = "id-tina.tech", username = "tina.tech", printedName = "Tina", employeeId = "E1",
            identity = new { method = "EmployeeBadgeInPerson", evidenceReference = "Badge" },
        });
        Assert.Equal(HttpStatusCode.Created, enroll.StatusCode);
        Assert.Equal("PendingCertification", (await enroll.JsonAsync()).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record))).StatusCode);

        var wrongCertify = await tech.PostJsonAsync("/api/signers/me/certification", new { username = "tina.tech", password = "nope", statementVersion = "1.0" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongCertify.StatusCode);
        Assert.Equal("Signature authentication failed", (await wrongCertify.JsonAsync()).GetProperty("title").GetString());

        var certify = await tech.PostJsonAsync("/api/signers/me/certification", new { username = "tina.tech", password = ApiFactory.Password, statementVersion = "1.0" });
        Assert.Equal("Active", (await certify.JsonAsync()).GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await tech.PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record, password: "nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await tech.PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record))).StatusCode);
    }

    [Fact]
    public async Task EverySigningNeedsThePasswordAgain()
    {
        await _f.ActivateAsync("tina.tech");
        var record = _f.Audit.AddRecord();
        var before = _f.Auth.Calls;
        await _f.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record, meaning: "Authorship"));
        await _f.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record, meaning: "Responsibility"));
        Assert.Equal(before + 2, _f.Auth.Calls);
    }

    [Fact]
    public async Task SigningAsSomeoneElse_IsRefusedAndAlerted()
    {
        await _f.ActivateAsync("tina.tech");
        var record = _f.Audit.AddRecord();
        var response = await _f.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record, username: "rob.reviewer"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var alerts = await (await _f.Manager().GetAsync("/api/security-alerts?minimumSeverity=Critical")).JsonAsync();
        Assert.Equal("IdentityMismatch", alerts[0].GetProperty("type").GetString());
        Assert.Equal("Critical", alerts[0].GetProperty("severity").GetString());
    }

    [Fact]
    public async Task RepeatedFailures_Lock_With423_AndAreReportedToTheSecurityUnit()
    {
        await _f.ActivateAsync("tina.tech");
        var record = _f.Audit.AddRecord();
        var tech = _f.Technician();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await tech.PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record, password: "wrong"))).StatusCode);
        }

        var locked = await tech.PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record));
        Assert.Equal(HttpStatusCode.Locked, locked.StatusCode);
        var problem = await locked.JsonAsync();
        Assert.Equal("Signing locked", problem.GetProperty("title").GetString());
        Assert.True(problem.GetProperty("lockedUntilUtc").GetDateTime() > DateTime.UtcNow);

        var alerts = await (await _f.Manager().GetAsync("/api/security-alerts")).JsonAsync();
        var types = alerts.EnumerateArray().Select(a => a.GetProperty("type").GetString()).ToList();
        Assert.Contains("FailedSignatureAuthentication", types);
        Assert.Contains("SignerLockedOut", types);
        Assert.Contains("AttemptWhileLocked", types);
        // The alerts also reached the RecordAuditTrails audit trail, under the signer's own token.
        Assert.NotEmpty(_f.Audit.OfType(AuditFields.AlertRecordType));
    }

    [Fact]
    public async Task ASecondSigner_ChainsOntoTheRecord_AndEditsAreShownOnTheSignatures()
    {
        await _f.ActivateAsync("tina.tech", "Tina Technician");
        await _f.ActivateAsync("mia.manager", "Mia Manager");
        var record = _f.Audit.AddRecord();
        await _f.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record));
        var approval = await _f.Manager().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record, meaning: "Approval", username: "mia.manager"));
        Assert.Equal(2, (await approval.JsonAsync()).GetProperty("sequence").GetInt64());

        var clean = await (await _f.Technician().GetAsync($"/api/records/{record}/signatures")).JsonAsync();
        Assert.True(clean.GetProperty("isIntact").GetBoolean());
        Assert.All(clean.GetProperty("signatures").EnumerateArray(), s => Assert.Equal("Valid", s.GetProperty("status").GetString()));

        _f.Audit.Modify(record);
        var changed = await (await _f.Technician().GetAsync($"/api/records/{record}/signatures")).JsonAsync();
        Assert.True(changed.GetProperty("isIntact").GetBoolean());
        Assert.All(changed.GetProperty("signatures").EnumerateArray(), s => Assert.Equal("RecordModifiedSinceSigning", s.GetProperty("status").GetString()));
        Assert.Equal(2, changed.GetProperty("record").GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task TamperingWithTheDatabase_IsDetected_AndRaisesACriticalAlert()
    {
        await _f.ActivateAsync("tina.tech");
        var record = _f.Audit.AddRecord();
        var id = (await (await _f.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record))).JsonAsync()).GetProperty("id").GetGuid();

        // Simulate someone with database access editing the signature behind the application's back.
        await _f.WithDbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE signatures SET SignerPrintedName = 'Mallory'"));

        var result = await (await _f.Technician().GetAsync($"/api/records/{record}/signatures")).JsonAsync();
        Assert.False(result.GetProperty("isIntact").GetBoolean());
        Assert.Equal("SealInvalid", result.GetProperty("signatures")[0].GetProperty("status").GetString());
        var alerts = await (await _f.Manager().GetAsync("/api/security-alerts?minimumSeverity=Critical")).JsonAsync();
        Assert.Contains(alerts.EnumerateArray(), a => a.GetProperty("type").GetString() == "SignatureIntegrityFailure");
    }

    [Fact]
    public async Task ASignatureIsReadableById_AndRevocableByAManagerOnly()
    {
        await _f.ActivateAsync("tina.tech");
        var record = _f.Audit.AddRecord();
        var id = (await (await _f.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record))).JsonAsync()).GetProperty("id").GetGuid();

        var read = await (await _f.Technician().GetAsync($"/api/signatures/{id}")).JsonAsync();
        Assert.Equal("Valid", read.GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await _f.Technician().PostJsonAsync($"/api/signatures/{id}/revoke", new { reason = "Signed in error" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Manager().PostJsonAsync($"/api/signatures/{id}/revoke", new { reason = "no" })).StatusCode);
        var revoked = await _f.Manager().PostJsonAsync($"/api/signatures/{id}/revoke", new { reason = "Signed in error" });
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        var body = await revoked.JsonAsync();
        Assert.Equal("Revoked", body.GetProperty("status").GetString());
        Assert.Equal("mia.manager", body.GetProperty("revocation").GetProperty("revokedByUsername").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await _f.Manager().PostJsonAsync($"/api/signatures/{id}/revoke", new { reason = "Signed in error" })).StatusCode);
        Assert.Equal("Revoked", _f.Audit.Records[id].Fields["status"]);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Technician().GetAsync($"/api/signatures/{Guid.NewGuid()}")).StatusCode);
    }

    [Theory]
    [InlineData("json", "application/json")]
    [InlineData("text", "text/plain")]
    [InlineData("html", "text/html")]
    [InlineData(null, "application/json")]
    public async Task Reports_AreAvailableInHumanReadableAndElectronicForm_WithADigest(string? format, string contentType)
    {
        await _f.ActivateAsync("tina.tech", "Tina Technician");
        var record = _f.Audit.AddRecord();
        await _f.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record));

        var response = await _f.Reviewer().GetAsync($"/api/records/{record}/signatures/report" + (format is null ? "" : $"?format={format}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith(contentType, response.Content.Headers.ContentType!.ToString());
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("Tina Technician", text);
        Assert.Contains("Authorship", text);
        var digest = response.Headers.GetValues("X-Content-SHA256").Single();
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))), digest);
    }

    [Fact]
    public async Task Meanings_AreListed()
    {
        var meanings = await (await _f.Technician().GetAsync("/api/meanings")).JsonAsync();
        Assert.Equal(["Authorship", "Responsibility", "Review", "Approval"], meanings.EnumerateArray().Select(m => m.GetProperty("code").GetString()));
        Assert.Equal("LabManager", meanings[3].GetProperty("minimumRole").GetString());
    }

    [Fact]
    public async Task BadRequests_AreAnswered_WithProblemDetails()
    {
        await _f.ActivateAsync("tina.tech");
        var record = _f.Audit.AddRecord();
        var tech = _f.Technician();

        var badMeaning = await tech.PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record, meaning: "Witnessed"));
        Assert.Equal(HttpStatusCode.BadRequest, badMeaning.StatusCode);
        Assert.Equal("application/problem+json", badMeaning.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Authorship", (await badMeaning.JsonAsync()).GetProperty("detail").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Reviewer().GetAsync($"/api/records/{record}/signatures/report?format=pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Manager().GetAsync("/api/signers?status=Bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Manager().GetAsync("/api/security-alerts?minimumSeverity=Bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await tech.PostAsync("/api/signatures", HttpExtensions.Raw("{bad json"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tech.GetAsync("/api/records/not-a-guid/signatures")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await tech.PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record, version: 9))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tech.PostJsonAsync("/api/signatures", HttpExtensions.SignBody(Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task WhenUpstreamServicesFail_NothingIsSigned_AndTheErrorIsHonest()
    {
        await _f.ActivateAsync("tina.tech");
        var record = _f.Audit.AddRecord();

        _f.Auth.Unavailable = true;
        var authDown = await _f.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, authDown.StatusCode);
        _f.Auth.Unavailable = false;

        _f.Audit.FailCreate = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _f.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record))).StatusCode);
        _f.Audit.FailCreate = false;

        Assert.Empty(_f.Audit.OfType(AuditFields.SignatureRecordType));
        var list = await (await _f.Technician().GetAsync($"/api/records/{record}/signatures")).JsonAsync();
        Assert.Equal(0, list.GetProperty("signatures").GetArrayLength());
    }

    [Fact]
    public async Task UnexpectedErrors_AreGeneric_AndLeakNothing()
    {
        _f.Audit.UnexpectedFailure = true;
        var response = await _f.Technician().GetAsync($"/api/records/{Guid.NewGuid()}/signatures");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret internal detail", text);
        Assert.Contains("An unexpected error occurred.", text);
    }

    [Fact]
    public async Task Sign_IsBlockedWhenReviewIsOverdue()
    {
        using var factory = new ApiFactory(new() { ["Signatures:ReviewIntervalMonths"] = "0" });
        await factory.InitializeDatabaseAsync();
        await factory.ActivateAsync("tina.tech");
        var record = factory.Audit.AddRecord();
        var response = await factory.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("overdue", (await response.JsonAsync()).GetProperty("detail").GetString());

        var due = await (await factory.Manager().GetAsync("/api/signers?reviewDue=true")).JsonAsync();
        Assert.Equal(1, due.GetArrayLength());
        var review = await factory.Admin().PostJsonAsync("/api/signers/id-tina.tech/review", new
        {
            reason = "Annual recertification", identity = new { method = "HumanResourcesRecord", evidenceReference = "HR file checked" },
        });
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
    }
}
