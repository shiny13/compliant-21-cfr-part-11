using System.Net;

namespace RecordAuditTrails.Api.Tests;

public class DocumentFlowTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private ApiFactory F => fixture.Factory;

    private static object NewDoc(string number, string role = "LabTechnician") =>
        new { documentNumber = number, title = "Backup procedure", category = "Maintenance", content = "Run nightly.", minimumReadRole = role };

    private async Task<Guid> CreateAsync(string number, string role = "LabTechnician")
    {
        var response = await F.Admin().PostJsonAsync("/api/documents", NewDoc(number, role));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("summary").GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task RevisionControlAccessControlAndDistributionLogging()
    {
        var number = "SOP-" + Guid.NewGuid().ToString("N")[..8];
        var id = await CreateAsync(number, "LabManager");
        var admin = F.Admin();

        // Revise with a change description: the history keeps every revision.
        var revised = await admin.PostJsonAsync($"/api/documents/{id}/revisions", new { content = "Run nightly and verify.", changeDescription = "Added verification step", expectedRevision = 1 });
        Assert.Equal(HttpStatusCode.Created, revised.StatusCode);
        Assert.Equal(2, (await revised.JsonAsync()).GetProperty("revision").GetProperty("revisionNumber").GetInt32());

        // Technicians cannot see the restricted document; managers can.
        Assert.Equal(HttpStatusCode.Forbidden, (await F.Technician().GetAsync($"/api/documents/{id}")).StatusCode);
        Assert.DoesNotContain((await (await F.Technician().GetAsync("/api/documents")).JsonAsync()).EnumerateArray(), d => d.GetProperty("id").GetGuid() == id);
        Assert.Contains((await (await F.Manager().GetAsync("/api/documents")).JsonAsync()).EnumerateArray(), d => d.GetProperty("id").GetGuid() == id);

        var current = await (await F.Manager().GetAsync($"/api/documents/{id}")).JsonAsync();
        Assert.Equal("Run nightly and verify.", current.GetProperty("revision").GetProperty("content").GetString());
        var first = await (await F.Manager().GetAsync($"/api/documents/{id}?revision=1")).JsonAsync();
        Assert.Equal("Run nightly.", first.GetProperty("revision").GetProperty("content").GetString());

        var history = await (await F.Manager().GetAsync($"/api/documents/{id}/revisions")).JsonAsync();
        Assert.Equal(2, history.GetArrayLength());
        Assert.Equal("Added verification step", history[1].GetProperty("changeDescription").GetString());
        Assert.Equal(history[0].GetProperty("hash").GetString(), history[1].GetProperty("previousHash").GetString());
        Assert.Equal("adam.admin", history[1].GetProperty("author").GetString());

        var verify = await (await F.Reviewer().GetAsync($"/api/documents/{id}/revisions/verify")).JsonAsync();
        Assert.True(verify.GetProperty("isValid").GetBoolean());

        var distribution = await F.Manager().PostJsonAsync($"/api/documents/{id}/distributions", new { recipient = "FDA inspector", note = "Inspection 2026-04" });
        Assert.Equal(HttpStatusCode.OK, distribution.StatusCode);
        Assert.Equal("Distribute", (await distribution.JsonAsync()).GetProperty("action").GetString());

        var log = (await (await F.Manager().GetAsync($"/api/documents/{id}/access-log")).JsonAsync()).EnumerateArray().ToList();
        Assert.Equal(["View", "View", "View", "Distribute"], log.Select(l => l.GetProperty("action").GetString()));
        Assert.Contains("FDA inspector", log[^1].GetProperty("details").GetString());
        Assert.Equal("mia.manager", log[^1].GetProperty("username").GetString());
        Assert.Single((await (await F.Manager().GetAsync($"/api/documents/{id}/access-log?skip=3&take=5")).JsonAsync()).EnumerateArray());
    }

    [Fact]
    public async Task ErrorsAreMappedToProblemResponses()
    {
        var number = "SOP-" + Guid.NewGuid().ToString("N")[..8];
        var id = await CreateAsync(number);
        var admin = F.Admin();

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostJsonAsync("/api/documents", NewDoc(number))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/documents", NewDoc("SOP-X", "Nobody"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostJsonAsync($"/api/documents/{id}/revisions", new { content = "changed", changeDescription = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostJsonAsync($"/api/documents/{id}/revisions", new { content = "changed", changeDescription = "valid reason", expectedRevision = 5 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/documents/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/documents/{id}?revision=42")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await F.Manager().PostJsonAsync($"/api/documents/{id}/distributions", new { recipient = " " })).StatusCode);
    }

    [Fact]
    public async Task DocumentRevisionTamperingIsDetected()
    {
        var id = await CreateAsync("SOP-" + Guid.NewGuid().ToString("N")[..8]);
        await F.ExecuteSqlAsync($"UPDATE document_revisions SET Content = 'quietly edited' WHERE DocumentId = '{id.ToString().ToUpperInvariant()}'");

        var verify = await (await F.Reviewer().GetAsync($"/api/documents/{id}/revisions/verify")).JsonAsync();

        Assert.False(verify.GetProperty("isValid").GetBoolean());
    }
}
