using System.Net;

namespace RecordSignatures.Api.Tests;

public class SignerLifecycleTests : IDisposable
{
    private readonly ApiFactory _f = new();

    public SignerLifecycleTests() => _f.InitializeDatabaseAsync().GetAwaiter().GetResult();

    public void Dispose() => _f.Dispose();

    private static object Enroll(string username, string employeeId = "E1") => new
    {
        userId = ApiFactory.UserId(username), username, printedName = $"Printed {username}", employeeId,
        identity = new { method = "GovernmentIssuedId", evidenceReference = "Passport sighted" },
    };

    private static object Identity => new { method = "EmployeeBadgeInPerson", evidenceReference = "Badge sighted by security" };

    [Fact]
    public async Task Enrollment_RecordsHowTheIdentityWasVerified_AndWhoDidIt()
    {
        var response = await _f.Admin().PostJsonAsync("/api/signers", Enroll("tina.tech"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/signers/id-tina.tech", response.Headers.Location!.ToString());
        var signer = await response.JsonAsync();
        Assert.Equal("adam.admin", signer.GetProperty("identity").GetProperty("verifiedByUsername").GetString());
        Assert.Equal("GovernmentIssuedId", signer.GetProperty("identity").GetProperty("method").GetString());
        Assert.Equal("PendingCertification", signer.GetProperty("status").GetString());
        Assert.True(signer.GetProperty("nextReviewDueUtc").GetDateTime() > DateTime.UtcNow.AddMonths(11));
    }

    [Fact]
    public async Task AdministratorsCannotEnrollThemselves()
    {
        var response = await _f.Admin().PostJsonAsync("/api/signers", Enroll("adam.admin"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("11.200(a)(3)", (await response.JsonAsync()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Identities_AreNeverSharedOrReassigned()
    {
        await _f.Admin().PostJsonAsync("/api/signers", Enroll("tina.tech", "E1"));
        Assert.Equal(HttpStatusCode.Conflict, (await _f.Admin().PostJsonAsync("/api/signers", Enroll("tina.tech", "E2"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _f.Admin().PostJsonAsync("/api/signers", Enroll("tom.tech", "E1"))).StatusCode);

        var revoke = await _f.Admin().PostJsonAsync("/api/signers/id-tina.tech/revoke", new { reason = "Left the company" });
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        var again = await _f.Admin().PostJsonAsync("/api/signers", Enroll("tina.tech", "E9"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("never reassigned", (await again.JsonAsync()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task EnrollmentValidation_ReturnsProblemDetails()
    {
        var response = await _f.Admin().PostJsonAsync("/api/signers", new { userId = "x", username = "x", printedName = "", employeeId = "e",
            identity = new { method = "Other", evidenceReference = "e" } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Printed name", (await response.JsonAsync()).GetProperty("detail").GetString());

        var noIdentity = await _f.Admin().PostJsonAsync("/api/signers", new { userId = "x", username = "x", printedName = "p", employeeId = "e" });
        Assert.Equal(HttpStatusCode.BadRequest, noIdentity.StatusCode);
        Assert.Contains("11.100(b)", (await noIdentity.JsonAsync()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ASignerSeesTheirOwnEnrollment_AndAManagerCanReadAnyone()
    {
        await _f.ActivateAsync("tina.tech", "Tina Technician");
        var me = await (await _f.Technician().GetAsync("/api/signers/me")).JsonAsync();
        Assert.Equal("Tina Technician", me.GetProperty("printedName").GetString());
        Assert.Equal("Active", me.GetProperty("status").GetString());

        var other = await (await _f.Manager().GetAsync("/api/signers/id-tina.tech")).JsonAsync();
        Assert.Equal(me.GetProperty("id").GetGuid(), other.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Manager().GetAsync("/api/signers/ghost")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.ClientFor("tom.tech").GetAsync("/api/signers/me")).StatusCode);
    }

    [Fact]
    public async Task TheCertificationIsPrintable()
    {
        await _f.ActivateAsync("tina.tech", "Tina Technician");
        var response = await _f.Manager().GetAsync("/api/signers/id-tina.tech/certification");
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType!.ToString());
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("CERTIFICATION OF ELECTRONIC SIGNATURE", text);
        Assert.Contains("Tina Technician", text);
        Assert.Contains("Acme Pharma", text);
        Assert.Contains("legally binding equivalent", text);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Manager().GetAsync("/api/signers/ghost/certification")).StatusCode);
    }

    [Fact]
    public async Task Listing_FiltersByStatus()
    {
        await _f.ActivateAsync("tina.tech");
        await _f.Admin().PostJsonAsync("/api/signers", Enroll("tom.tech", "E2"));
        Assert.Equal(2, (await (await _f.Manager().GetAsync("/api/signers")).JsonAsync()).GetArrayLength());
        var pending = await (await _f.Manager().GetAsync("/api/signers?status=pendingcertification")).JsonAsync();
        Assert.Equal("id-tom.tech", pending[0].GetProperty("userId").GetString());
        Assert.Equal(1, (await (await _f.Manager().GetAsync("/api/signers?skip=1&take=5")).JsonAsync()).GetArrayLength());
        Assert.Equal(0, (await (await _f.Manager().GetAsync("/api/signers?reviewDue=true")).JsonAsync()).GetArrayLength());
    }

    [Fact]
    public async Task LossManagement_SuspendRevokesSessions_BlocksSigning_AndAlerts()
    {
        await _f.ActivateAsync("tina.tech");
        var record = _f.Audit.AddRecord();

        var suspend = await _f.Admin().PostJsonAsync("/api/signers/id-tina.tech/suspend", new { reason = "Password written on a note" });
        Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
        var result = await suspend.JsonAsync();
        Assert.Equal("Suspended", result.GetProperty("signer").GetProperty("status").GetString());
        Assert.True(result.GetProperty("sessionsRevoked").GetBoolean());
        Assert.Equal(["id-tina.tech"], _f.Auth.RevokedUsers);

        var blocked = await _f.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record));
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Contains("suspended", (await blocked.JsonAsync()).GetProperty("detail").GetString());

        var alerts = await (await _f.Manager().GetAsync("/api/security-alerts")).JsonAsync();
        Assert.Contains(alerts.EnumerateArray(), a => a.GetProperty("type").GetString() == "SignerDeauthorized");
    }

    [Fact]
    public async Task Reinstatement_NeedsAFreshIdentityCheck_AndRestoresSigning()
    {
        await _f.ActivateAsync("tina.tech");
        var record = _f.Audit.AddRecord();
        await _f.Admin().PostJsonAsync("/api/signers/id-tina.tech/suspend", new { reason = "Badge lost", revokeSessions = false });

        var noIdentity = await _f.Admin().PostJsonAsync("/api/signers/id-tina.tech/reinstate", new { reason = "Badge found" });
        Assert.Equal(HttpStatusCode.BadRequest, noIdentity.StatusCode);

        var ok = await _f.Admin().PostJsonAsync("/api/signers/id-tina.tech/reinstate", new { reason = "Badge found and identity re-checked", identity = Identity });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.JsonAsync();
        Assert.Equal("Active", body.GetProperty("signer").GetProperty("status").GetString());
        Assert.Equal(JsonValueKindNull, body.GetProperty("sessionsRevoked").ValueKind);
        Assert.Equal(HttpStatusCode.Created, (await _f.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(record))).StatusCode);

        // Reinstating something that is not suspended is a conflict.
        Assert.Equal(HttpStatusCode.Conflict, (await _f.Admin().PostJsonAsync("/api/signers/id-tina.tech/reinstate", new { reason = "Again please", identity = Identity })).StatusCode);
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task Revocation_IsPermanent()
    {
        await _f.ActivateAsync("tina.tech");
        var revoke = await _f.Admin().PostJsonAsync("/api/signers/id-tina.tech/revoke", new { reason = "Credentials confirmed stolen" });
        Assert.Equal("Revoked", (await revoke.JsonAsync()).GetProperty("signer").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await _f.Admin().PostJsonAsync("/api/signers/id-tina.tech/revoke", new { reason = "Revoke it twice" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _f.Admin().PostJsonAsync("/api/signers/id-tina.tech/reinstate", new { reason = "Undo the revoke", identity = Identity })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.Technician().PostJsonAsync("/api/signatures", HttpExtensions.SignBody(_f.Audit.AddRecord()))).StatusCode);
    }

    [Fact]
    public async Task AdministratorsCannotAdministerThemselves_OrUnknownSigners()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _f.Admin().PostJsonAsync("/api/signers/id-adam.admin/suspend", new { reason = "Suspend myself" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _f.Admin().PostJsonAsync("/api/signers/ghost/suspend", new { reason = "Who is that" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.Admin().PostJsonAsync("/api/signers/ghost/suspend", new { reason = "no" })).StatusCode);
    }

    [Fact]
    public async Task EveryChange_IsWrittenToTheAuditTrail_UnderTheActorsOwnToken()
    {
        await _f.ActivateAsync("tina.tech");
        await _f.Admin().PostJsonAsync("/api/signers/id-tina.tech/suspend", new { reason = "Badge lost", revokeSessions = false });
        var record = Assert.Single(_f.Audit.OfType("SignerEnrollment"));
        Assert.Equal(3, record.Reasons.Count);
        Assert.Contains("enrolled", record.Reasons[0]);
        Assert.Contains("certification", record.Reasons[1]);
        Assert.Contains("Badge lost", record.Reasons[2]);
        Assert.Equal("Suspended", record.Fields["status"]);
        Assert.Equal(3, record.Tokens.Distinct().Count());
    }

    [Fact]
    public async Task IfTheAuditTrailRejectsAChange_ItDoesNotHappen()
    {
        await _f.ActivateAsync("tina.tech");
        _f.Audit.FailUpdate = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _f.Admin().PostJsonAsync("/api/signers/id-tina.tech/suspend", new { reason = "Badge lost" })).StatusCode);
        _f.Audit.FailUpdate = false;
        Assert.Equal("Active", (await (await _f.Manager().GetAsync("/api/signers/id-tina.tech")).JsonAsync()).GetProperty("status").GetString());
    }
}
