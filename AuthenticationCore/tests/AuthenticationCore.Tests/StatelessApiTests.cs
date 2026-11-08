using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using AuthenticationCore.Shared.InternalContext;
using AuthenticationCore.Shared.Options;
using AuthenticationCore.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthenticationCore.Tests;

public class StatelessApiTests(AuthStack stack) : IClassFixture<AuthStack>
{
    private HttpClient Api => stack.Sample.CreateClient();

    [Fact]
    public async Task Public_endpoint_needs_no_token_but_protected_endpoint_does()
    {
        Assert.Equal(HttpStatusCode.OK, (await Api.GetAsync("/samples/public")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.GetAsync("/samples/whoami")).StatusCode);
    }

    [Theory]
    [InlineData("admin", "technician", HttpStatusCode.OK)]
    [InlineData("admin", "admin", HttpStatusCode.OK)]
    [InlineData("manager", "reviewer", HttpStatusCode.OK)]
    [InlineData("manager", "manager", HttpStatusCode.OK)]
    [InlineData("manager", "admin", HttpStatusCode.Forbidden)]
    [InlineData("reviewer", "technician", HttpStatusCode.OK)]
    [InlineData("reviewer", "manager", HttpStatusCode.Forbidden)]
    [InlineData("technician", "technician", HttpStatusCode.OK)]
    [InlineData("technician", "reviewer", HttpStatusCode.Forbidden)]
    [InlineData("technician", "admin", HttpStatusCode.Forbidden)]
    public async Task Higher_roles_inherit_access_to_lower_role_endpoints(string user, string endpoint, HttpStatusCode expected)
    {
        var tokens = await stack.LoginAsync(user);

        var response = await Api.SendAsync(AuthStack.Get($"/samples/{endpoint}", tokens.AccessToken));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Tampered_token_is_rejected()
    {
        var tokens = await stack.LoginAsync("technician");
        var tampered = tokens.AccessToken[..^3] + (tokens.AccessToken.EndsWith("AAA") ? "BBB" : "AAA");

        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SendAsync(AuthStack.Get("/samples/whoami", tampered))).StatusCode);
    }

    [Fact]
    public async Task Token_signed_with_an_unknown_key_is_rejected_even_if_it_claims_to_be_an_administrator()
    {
        using var rsa = RSA.Create(2048);
        var options = stack.Sample.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthenticationCoreOptions>>().Value;
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([
                new Claim("sub", Guid.NewGuid().ToString()),
                new Claim("jti", Guid.NewGuid().ToString("N")),
                new Claim("role", "Administrator"),
            ]),
            Issuer = options.Issuer,
            Audience = options.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "attacker" }, SecurityAlgorithms.RsaSha256),
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SendAsync(AuthStack.Get("/samples/admin", forged))).StatusCode);
    }

    [Fact]
    public async Task Validation_makes_no_network_calls_to_the_auth_service()
    {
        var tokens = await stack.LoginAsync("technician");
        var before = stack.JwksRequestCount;

        for (var i = 0; i < 25; i++)
        {
            var response = await Api.SendAsync(AuthStack.Get("/samples/whoami", tokens.AccessToken));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(before, stack.JwksRequestCount);
    }

    [Fact]
    public async Task Revocation_reaches_the_api_through_the_background_feed()
    {
        var tokens = await stack.LoginAsync("propagation-user");
        Assert.Equal(HttpStatusCode.OK, (await Api.SendAsync(AuthStack.Get("/samples/whoami", tokens.AccessToken))).StatusCode);

        var logout = await stack.Service.CreateClient().SendAsync(AuthStack.Post("/auth/logout", tokens.AccessToken));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var status = HttpStatusCode.OK;
        await AuthStack.WaitUntilAsync(() =>
        {
            status = Api.SendAsync(AuthStack.Get("/samples/whoami", tokens.AccessToken)).GetAwaiter().GetResult().StatusCode;
            return status == HttpStatusCode.Unauthorized;
        });
        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    [Fact]
    public async Task Gateway_hydrated_internal_context_token_authenticates_without_a_bearer_token()
    {
        var issuer = stack.Sample.Services.GetRequiredService<InternalContextTokenIssuer>();
        var external = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("preferred_username", "gateway-user"),
            new Claim("role", "Reviewer"),
        ], "test"));

        var internalToken = issuer.Issue(external, correlationId: "corr-1");

        using var allowed = new HttpRequestMessage(HttpMethod.Get, "/samples/reviewer");
        allowed.Headers.Add(InternalContextOptions.DefaultHeaderName, internalToken);
        Assert.Equal(HttpStatusCode.OK, (await Api.SendAsync(allowed)).StatusCode);

        using var denied = new HttpRequestMessage(HttpMethod.Get, "/samples/manager");
        denied.Headers.Add(InternalContextOptions.DefaultHeaderName, internalToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await Api.SendAsync(denied)).StatusCode);
    }

    [Fact]
    public async Task Internal_context_token_signed_with_the_wrong_key_is_rejected()
    {
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim("sub", "x"), new Claim("role", "Administrator")]),
            Issuer = "gateway",
            Audience = "internal-services",
            Expires = DateTime.UtcNow.AddSeconds(30),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)), SecurityAlgorithms.HmacSha256),
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/samples/admin");
        request.Headers.Add(InternalContextOptions.DefaultHeaderName, forged);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Api.SendAsync(request)).StatusCode);
    }
}
