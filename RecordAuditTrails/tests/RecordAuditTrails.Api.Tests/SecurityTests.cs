using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace RecordAuditTrails.Api.Tests;

public class SecurityTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private static readonly string[] Roles = ["LabTechnician", "Reviewer", "LabManager", "Administrator"];

    // method, path, minimum role (index into Roles)
    public static TheoryData<string, string, int> Endpoints()
    {
        var id = Guid.NewGuid();
        var data = new TheoryData<string, string, int>
        {
            { "POST", "/api/records", 0 },
            { "GET", "/api/records", 0 },
            { "GET", $"/api/records/{id}", 0 },
            { "PUT", $"/api/records/{id}", 0 },
            { "DELETE", $"/api/records/{id}", 0 },
            { "GET", $"/api/records/{id}/audit-trail", 1 },
            { "GET", $"/api/records/{id}/audit-trail/verify", 1 },
            { "GET", $"/api/records/{id}/export", 1 },
            { "GET", $"/api/records/{id}/retention", 1 },
            { "GET", "/api/audit-trail", 1 },
            { "GET", "/api/audit-trail/verify", 2 },
            { "GET", "/api/retention/expired", 2 },
            { "POST", "/api/documents", 3 },
            { "GET", "/api/documents", 0 },
            { "GET", $"/api/documents/{id}", 0 },
            { "POST", $"/api/documents/{id}/revisions", 3 },
            { "GET", $"/api/documents/{id}/revisions", 0 },
            { "GET", $"/api/documents/{id}/revisions/verify", 1 },
            { "POST", $"/api/documents/{id}/distributions", 2 },
            { "GET", $"/api/documents/{id}/access-log", 2 },
        };
        return data;
    }

    private static HttpRequestMessage Request(string method, string path) => new(new HttpMethod(method), path)
    {
        Content = method is "GET" ? null : HttpExtensions.Raw("{}"),
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task EveryEndpointRequiresAToken(string method, string path, int _)
    {
        var response = await fixture.Factory.CreateClient().SendAsync(Request(method, path));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task RolesBelowTheMinimumAreForbiddenAndTheMinimumAndAboveAreAllowed(string method, string path, int minimum)
    {
        for (var i = 0; i < Roles.Length; i++)
        {
            var client = fixture.Factory.ClientFor($"user-{Roles[i]}", Roles[i]);
            var response = await client.SendAsync(Request(method, path));
            if (i < minimum)
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
            else
            {
                Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task TokensFailingValidationAreRejected()
    {
        using var otherKey = RSA.Create(2048);
        var f = fixture.Factory;
        var bad = new Dictionary<string, string>
        {
            ["foreign signature"] = f.Token(signingKey: otherKey),
            ["expired"] = f.Token(lifetime: TimeSpan.FromMinutes(-10)),
            ["wrong audience"] = f.Token(audience: "someone-else"),
            ["wrong issuer"] = f.Token(issuer: "https://evil.example"),
            ["garbage"] = "not.a.jwt",
        };
        foreach (var (name, token) in bad)
        {
            var client = f.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await client.GetAsync("/api/records");
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{name} should be rejected but got {response.StatusCode}");
        }
    }

    [Fact]
    public async Task TokenWithoutARoleIsForbidden()
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Factory.Token(omitRole: true));
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/records")).StatusCode);
    }

    [Fact]
    public async Task TokenWithoutAUsernameCannotAttributeActions()
    {
        var client = fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Factory.Token(omitUsername: true));

        var response = await client.PostJsonAsync("/api/records", new { recordType = "T", title = "x" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty((await fixture.Factory.Technician().GetFromJsonAsync<System.Text.Json.JsonElement[]>("/api/records?recordType=T"))!);
    }

    [Fact]
    public async Task HealthAndDocumentationAreAnonymous()
    {
        var client = fixture.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.yaml")).StatusCode);
    }
}
