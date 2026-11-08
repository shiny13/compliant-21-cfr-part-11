using System.Net;
using System.Security.Cryptography;

namespace RecordSignatures.Api.Tests;

public class SecurityTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private readonly ApiFactory _f = fixture.Factory;

    public static TheoryData<string, string> Endpoints() => new()
    {
        { "POST", "/api/signers" },
        { "GET", "/api/signers" },
        { "GET", "/api/signers/me" },
        { "POST", "/api/signers/me/certification" },
        { "GET", "/api/signers/someone" },
        { "GET", "/api/signers/someone/certification" },
        { "POST", "/api/signers/someone/suspend" },
        { "POST", "/api/signers/someone/reinstate" },
        { "POST", "/api/signers/someone/revoke" },
        { "POST", "/api/signers/someone/review" },
        { "GET", "/api/meanings" },
        { "POST", "/api/signatures" },
        { "GET", "/api/signatures/11111111-1111-1111-1111-111111111111" },
        { "POST", "/api/signatures/11111111-1111-1111-1111-111111111111/revoke" },
        { "GET", "/api/records/11111111-1111-1111-1111-111111111111/signatures" },
        { "GET", "/api/records/11111111-1111-1111-1111-111111111111/signatures/report" },
        { "GET", "/api/security-alerts" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task EveryEndpoint_RequiresAToken(string method, string url)
    {
        var response = await _f.CreateClient().SendJsonAsync(new HttpMethod(method), url, method == "POST" ? new { } : null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<HttpStatusCode> StatusWithToken(string token)
    {
        var client = _f.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return (await client.GetAsync("/api/meanings")).StatusCode;
    }

    [Fact]
    public async Task ForgedExpiredAndMisaddressedTokens_AreRejected()
    {
        Assert.Equal(HttpStatusCode.OK, await StatusWithToken(_f.Token()));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusWithToken(_f.Token(lifetime: TimeSpan.FromMinutes(-30))));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusWithToken(_f.Token(audience: "someone-else")));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusWithToken(_f.Token(issuer: "https://evil.example")));
        using var attacker = RSA.Create(2048);
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusWithToken(_f.Token(signingKey: attacker)));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusWithToken("not-a-jwt"));
    }

    [Fact]
    public async Task ATokenWithoutAUsernameOrRole_CannotActAsAnyone()
    {
        var noName = _f.CreateClient();
        noName.DefaultRequestHeaders.Authorization = new("Bearer", _f.Token(omitUsername: true));
        Assert.Equal(HttpStatusCode.Forbidden, (await noName.GetAsync("/api/signers/me")).StatusCode);

        var noRole = _f.CreateClient();
        noRole.DefaultRequestHeaders.Authorization = new("Bearer", _f.Token(omitRole: true));
        Assert.Equal(HttpStatusCode.Forbidden, (await noRole.GetAsync("/api/meanings")).StatusCode);
    }

    [Fact]
    public async Task OperatorFactory_RejectsAnUnreadableBearerHeader()
    {
        // A principal can exist without a Bearer header only in odd hosting; the factory must still refuse to act.
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim("sub", "u1"), new System.Security.Claims.Claim("preferred_username", "n"), new System.Security.Claims.Claim("role", "Reviewer"),
            ], "test")),
        };
        Assert.Throws<RecordSignatures.Core.Exceptions.ForbiddenException>(() => RecordSignatures.Api.Security.OperatorFactory.FromHttpContext(context));
        context.Request.Headers.Authorization = "Bearer   ";
        Assert.Throws<RecordSignatures.Core.Exceptions.ForbiddenException>(() => RecordSignatures.Api.Security.OperatorFactory.FromHttpContext(context));
        context.Request.Headers.Authorization = "bearer abc";
        var op = RecordSignatures.Api.Security.OperatorFactory.FromHttpContext(context);
        Assert.Equal("abc", op.AccessToken);
        Assert.Equal(AuthenticationCore.Shared.Roles.UserRole.Reviewer, op.Role);
    }

    [Theory]
    [InlineData("tina.tech", "POST", "/api/signers", HttpStatusCode.Forbidden)]
    [InlineData("rob.reviewer", "POST", "/api/signers", HttpStatusCode.Forbidden)]
    [InlineData("mia.manager", "POST", "/api/signers", HttpStatusCode.Forbidden)]
    [InlineData("tina.tech", "GET", "/api/signers", HttpStatusCode.Forbidden)]
    [InlineData("rob.reviewer", "GET", "/api/signers", HttpStatusCode.Forbidden)]
    [InlineData("mia.manager", "GET", "/api/signers", HttpStatusCode.OK)]
    [InlineData("adam.admin", "GET", "/api/signers", HttpStatusCode.OK)]
    [InlineData("tina.tech", "GET", "/api/security-alerts", HttpStatusCode.Forbidden)]
    [InlineData("rob.reviewer", "GET", "/api/security-alerts", HttpStatusCode.Forbidden)]
    [InlineData("mia.manager", "GET", "/api/security-alerts", HttpStatusCode.OK)]
    [InlineData("tina.tech", "POST", "/api/signers/x/suspend", HttpStatusCode.Forbidden)]
    [InlineData("mia.manager", "POST", "/api/signers/x/revoke", HttpStatusCode.Forbidden)]
    [InlineData("tina.tech", "POST", "/api/signatures/11111111-1111-1111-1111-111111111111/revoke", HttpStatusCode.Forbidden)]
    [InlineData("rob.reviewer", "POST", "/api/signatures/11111111-1111-1111-1111-111111111111/revoke", HttpStatusCode.Forbidden)]
    [InlineData("tina.tech", "GET", "/api/records/11111111-1111-1111-1111-111111111111/signatures/report", HttpStatusCode.Forbidden)]
    [InlineData("tina.tech", "GET", "/api/signers/someone/certification", HttpStatusCode.Forbidden)]
    [InlineData("tina.tech", "GET", "/api/meanings", HttpStatusCode.OK)]
    public async Task RolePolicies_AreEnforced(string user, string method, string url, HttpStatusCode expected)
    {
        var response = await _f.ClientFor(user).SendJsonAsync(new HttpMethod(method), url,
            method == "POST" ? new { reason = "a valid reason", userId = "x" } : null);
        Assert.Equal(expected, response.StatusCode);
    }
}
