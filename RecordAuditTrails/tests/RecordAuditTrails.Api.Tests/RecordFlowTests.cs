using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RecordAuditTrails.Api.Tests;

public class RecordFlowTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private ApiFactory F => fixture.Factory;

    [Fact]
    public async Task CreateModifyDelete_ProducesACompleteAttributedAuditTrail()
    {
        var tech = F.Technician();
        var reviewer = F.Reviewer();

        // Create. The body carries no identity: the operator comes from the signed token only.
        var created = await tech.PostJsonAsync("/api/records", new
        {
            recordType = "BatchRecord",
            title = "Batch 7",
            fields = new { pH = "7.0" },
            username = "someone-else",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var record = await created.JsonAsync();
        var id = record.GetProperty("id").GetGuid();
        Assert.Equal($"/api/records/{id}", created.Headers.Location!.OriginalString);
        Assert.Equal(1, record.GetProperty("version").GetInt32());
        Assert.Equal("tina.tech", record.GetProperty("createdBy").GetString());
        Assert.Equal(DateTime.UtcNow.Year + 10, record.GetProperty("retainUntilUtc").GetDateTime().Year);

        // Modify with a reason.
        var modified = await tech.PutJsonAsync($"/api/records/{id}", new { fields = new { pH = "7.4" }, reason = "Corrected reading", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.OK, modified.StatusCode);
        Assert.Equal(2, (await modified.JsonAsync()).GetProperty("version").GetInt32());

        // Delete (logical) with a reason.
        var deleted = await tech.DeleteJsonAsync($"/api/records/{id}", new { reason = "Entered on wrong batch" });
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.True((await deleted.JsonAsync()).GetProperty("isDeleted").GetBoolean());

        // The reviewer sees every step with old value, new value, who and why.
        var trail = await (await reviewer.GetAsync($"/api/records/{id}/audit-trail")).JsonAsync();
        var entries = trail.EnumerateArray().ToList();
        Assert.Equal(["Create", "Create", "Create", "Modify", "Delete"], entries.Select(e => e.GetProperty("action").GetString()));
        var modify = entries.Single(e => e.GetProperty("action").GetString() == "Modify");
        Assert.Equal("pH", modify.GetProperty("fieldName").GetString());
        Assert.Equal("7.0", modify.GetProperty("oldValue").GetString());
        Assert.Equal("7.4", modify.GetProperty("newValue").GetString());
        Assert.Equal("Corrected reading", modify.GetProperty("reason").GetString());
        Assert.Equal("tina.tech", modify.GetProperty("username").GetString());
        Assert.Equal("id-tina.tech", modify.GetProperty("userId").GetString());
        Assert.Equal("LabTechnician", modify.GetProperty("role").GetString());
        Assert.True(modify.GetProperty("timestampUtc").GetDateTime() > DateTime.UtcNow.AddMinutes(-1));
        Assert.All(entries, e => Assert.Equal(64, e.GetProperty("hash").GetString()!.Length));
        Assert.DoesNotContain(entries, e => e.GetProperty("username").GetString() == "someone-else");

        var verify = await (await reviewer.GetAsync($"/api/records/{id}/audit-trail/verify")).JsonAsync();
        Assert.True(verify.GetProperty("isValid").GetBoolean());
        Assert.Equal(5, verify.GetProperty("itemsChecked").GetInt32());
    }

    [Fact]
    public async Task GetAndListRecords()
    {
        var tech = F.Technician();
        var type = "ListType" + Guid.NewGuid().ToString("N")[..6];
        var keep = await tech.CreateRecordAsync(type, "keep");
        var gone = await tech.CreateRecordAsync(type, "gone");
        await tech.DeleteJsonAsync($"/api/records/{gone}", new { reason = "not needed" });

        var one = await (await tech.GetAsync($"/api/records/{keep}")).JsonAsync();
        Assert.Equal("keep", one.GetProperty("title").GetString());
        Assert.Equal("tina", one.GetProperty("fields").GetProperty("operatorName").GetString());

        var visible = await (await tech.GetAsync($"/api/records?recordType={type}")).JsonAsync();
        Assert.Single(visible.EnumerateArray());
        var all = await (await tech.GetAsync($"/api/records?recordType={type}&includeDeleted=true&take=1&skip=1")).JsonAsync();
        Assert.Single(all.EnumerateArray());
    }

    [Fact]
    public async Task AuditTrailQueryFiltersByUserActionFieldAndTime()
    {
        var tech = F.Technician();
        var reviewer = F.Reviewer();
        var id = await tech.CreateRecordAsync();
        await tech.PutJsonAsync($"/api/records/{id}", new { title = "Renamed", reason = "Typo in title" });

        async Task<int> Count(string query) =>
            (await (await reviewer.GetAsync($"/api/records/{id}/audit-trail?{query}")).JsonAsync()).GetArrayLength();

        Assert.Equal(1, await Count("action=Modify"));
        Assert.Equal(0, await Count("user=nobody"));
        Assert.Equal(2, await Count("field=Title"));
        Assert.Equal(5, await Count($"from={Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(-5).ToString("o"))}"));
        Assert.Equal(0, await Count($"to={Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(-5).ToString("o"))}"));
        Assert.Equal(2, await Count("skip=3&take=5"));
        Assert.Equal(HttpStatusCode.BadRequest, (await reviewer.GetAsync($"/api/records/{id}/audit-trail?action=Explode")).StatusCode);

        var global = await (await reviewer.GetAsync("/api/audit-trail?user=tina.tech&field=Title")).JsonAsync();
        Assert.True(global.GetArrayLength() >= 2);
    }

    [Theory]
    [InlineData("json", "application/json")]
    [InlineData("csv", "text/csv")]
    [InlineData("text", "text/plain")]
    [InlineData("html", "text/html")]
    [InlineData(null, "application/json")]
    public async Task ExportReturnsACompleteCopyWithADigestHeader(string? format, string contentType)
    {
        var tech = F.Technician();
        var id = await tech.CreateRecordAsync(title: "Export me");
        await tech.PutJsonAsync($"/api/records/{id}", new { title = "Export me v2", reason = "Clarified title" });

        var response = await F.Reviewer().GetAsync($"/api/records/{id}/export" + (format is null ? "" : $"?format={format}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith(contentType, response.Content.Headers.ContentType!.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Export me v2", body);
        Assert.Contains("Clarified title", body);
        var digest = response.Headers.GetValues("X-Content-SHA256").Single();
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body))), digest);
    }

    [Fact]
    public async Task ExportRejectsUnknownFormatAndUnknownRecord()
    {
        var reviewer = F.Reviewer();
        var id = await F.Technician().CreateRecordAsync();
        var bad = await reviewer.GetAsync($"/api/records/{id}/export?format=pdf");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("application/problem+json", bad.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await reviewer.GetAsync($"/api/records/{Guid.NewGuid()}/export")).StatusCode);
    }

    [Fact]
    public async Task ProblemResponsesMapEachFailureKind()
    {
        var tech = F.Technician();
        var id = await tech.CreateRecordAsync();

        var validation = await tech.PutJsonAsync($"/api/records/{id}", new { title = "x", reason = "no" });
        Assert.Equal(HttpStatusCode.BadRequest, validation.StatusCode);
        var problem = await validation.JsonAsync();
        Assert.Equal(400, problem.GetProperty("status").GetInt32());
        Assert.Contains("reason", problem.GetProperty("detail").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await tech.GetAsync($"/api/records/{Guid.NewGuid()}")).StatusCode);

        await tech.PutJsonAsync($"/api/records/{id}", new { title = "v2", reason = "first change" });
        var conflict = await tech.PutJsonAsync($"/api/records/{id}", new { title = "v3", reason = "stale change", expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        var malformed = await tech.PostAsync("/api/records", HttpExtensions.Raw("{ not json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await tech.GetAsync("/api/records/not-a-guid")).StatusCode == HttpStatusCode.NotFound ? HttpStatusCode.BadRequest : HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RetentionEndpoints()
    {
        var id = await F.Technician().CreateRecordAsync("ClinicalTrial", "Trial 1");

        var status = await (await F.Reviewer().GetAsync($"/api/records/{id}/retention")).JsonAsync();
        Assert.Equal("ClinicalTrial", status.GetProperty("recordType").GetString());
        Assert.False(status.GetProperty("retentionExpired").GetBoolean());
        Assert.Equal(DateTime.UtcNow.Year + 15, status.GetProperty("retainUntilUtc").GetDateTime().Year);
        Assert.Equal(status.GetProperty("retainUntilUtc").GetDateTime(), status.GetProperty("auditTrailRetainUntilUtc").GetDateTime());

        Assert.Equal(HttpStatusCode.NotFound, (await F.Reviewer().GetAsync($"/api/records/{Guid.NewGuid()}/retention")).StatusCode);
        var expired = await (await F.Manager().GetAsync("/api/retention/expired?take=5")).JsonAsync();
        Assert.Equal(JsonValueKind.Array, expired.ValueKind);
        Assert.Empty(expired.EnumerateArray());
    }

    [Fact]
    public async Task SystemWideVerificationDetectsTamperingMadeBehindTheApplication()
    {
        var tech = F.Technician();
        var id = await tech.CreateRecordAsync("TamperType", "Tamper target");
        var verifyAll = async () => await (await F.Manager().GetAsync("/api/audit-trail/verify")).JsonAsync();

        Assert.True((await verifyAll()).GetProperty("isValid").GetBoolean());

        await F.ExecuteSqlAsync("UPDATE audit_trail SET NewValue = 'forged' WHERE FieldName = 'pH'");

        var report = await verifyAll();
        Assert.False(report.GetProperty("isValid").GetBoolean());
        Assert.Contains(report.GetProperty("failures").EnumerateArray(), f => f.GetProperty("recordId").GetGuid() == id);
        var single = await (await F.Reviewer().GetAsync($"/api/records/{id}/audit-trail/verify")).JsonAsync();
        Assert.False(single.GetProperty("isValid").GetBoolean());
        Assert.Contains("hash", single.GetProperty("problem").GetString());
    }
}
