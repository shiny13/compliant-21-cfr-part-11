using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthenticationCore.Service.Endpoints;
using AuthenticationCore.Shared.Revocation;
using AuthenticationCore.Tests.Support;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AuthenticationCore.Tests;

public class AuthServiceTests(AuthStack stack) : IClassFixture<AuthStack>
{
    private HttpClient Client => stack.Service.CreateClient();

    [Fact]
    public async Task Login_issues_a_short_lived_rs256_token_carrying_the_user_roles()
    {
        var tokens = await stack.LoginAsync("technician");
        var jwt = new JsonWebTokenHandler().ReadJsonWebToken(tokens.AccessToken);

        Assert.Equal("Bearer", tokens.TokenType);
        Assert.Equal(300, tokens.ExpiresIn);
        Assert.Equal("RS256", jwt.Alg);
        Assert.False(string.IsNullOrEmpty(jwt.Kid));
        Assert.Equal(TimeSpan.FromMinutes(5), jwt.ValidTo - jwt.ValidFrom);
        Assert.Contains(jwt.Claims, c => c.Type == "role" && c.Value == "LabTechnician");
        Assert.Contains(jwt.Claims, c => c.Type == "jti");
    }

    [Fact]
    public async Task Failed_logins_look_identical_for_unknown_users_and_wrong_passwords()
    {
        var unknown = await Client.PostAsJsonAsync("/auth/login", new LoginRequest("nobody", "whatever"));
        var wrong = await Client.PostAsJsonAsync("/auth/login", new LoginRequest("reviewer", "wrong-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await wrong.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Account_locks_after_repeated_failures_even_with_the_right_password()
    {
        for (var i = 0; i < 3; i++)
        {
            var bad = await Client.PostAsJsonAsync("/auth/login", new LoginRequest("lockout-user", "wrong"));
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }

        var good = await Client.PostAsJsonAsync("/auth/login", new LoginRequest("lockout-user", AuthStack.PasswordFor("lockout-user")));

        Assert.Equal(HttpStatusCode.Unauthorized, good.StatusCode);
    }

    [Fact]
    public async Task Me_requires_a_valid_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync("/auth/me")).StatusCode);

        var tokens = await stack.LoginAsync("reviewer");
        var me = await Client.SendAsync(AuthStack.Get("/auth/me", tokens.AccessToken));

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = await me.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("reviewer", body.GetProperty("username").GetString());
        Assert.Equal("Reviewer", body.GetProperty("highestRole").GetString());
    }

    [Fact]
    public async Task Logout_revokes_the_access_token_immediately()
    {
        var tokens = await stack.LoginAsync("logout-user");
        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(AuthStack.Get("/auth/me", tokens.AccessToken))).StatusCode);

        var logout = await Client.SendAsync(AuthStack.Post("/auth/logout", tokens.AccessToken, new LogoutRequest(tokens.RefreshToken)));
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(AuthStack.Get("/auth/me", tokens.AccessToken))).StatusCode);
        var refresh = await Client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(tokens.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_tokens_and_reuse_of_an_old_token_revokes_the_session()
    {
        var first = await stack.LoginAsync("refresh-user");

        var rotated = await Client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var second = (await rotated.Content.ReadFromJsonAsync<TokenResponse>())!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        var replay = await Client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(first.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(AuthStack.Get("/auth/me", second.AccessToken))).StatusCode);
        var afterReplay = await Client.PostAsJsonAsync("/auth/refresh", new RefreshRequest(second.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, afterReplay.StatusCode);
    }

    [Fact]
    public async Task Only_administrators_can_revoke_a_user()
    {
        var target = await stack.LoginAsync("revoke-target");
        var me = await Client.SendAsync(AuthStack.Get("/auth/me", target.AccessToken));
        var targetId = (await me.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("subject").GetGuid();

        var technician = await stack.LoginAsync("technician");
        var denied = await Client.SendAsync(AuthStack.Post("/auth/revoke", technician.AccessToken, new RevokeRequest(null, targetId)));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client.SendAsync(AuthStack.Get("/auth/me", target.AccessToken))).StatusCode);

        var admin = await stack.LoginAsync("admin");
        var allowed = await Client.SendAsync(AuthStack.Post("/auth/revoke", admin.AccessToken, new RevokeRequest(null, targetId)));
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.SendAsync(AuthStack.Get("/auth/me", target.AccessToken))).StatusCode);
    }

    [Fact]
    public async Task Revocation_feed_requires_the_service_key()
    {
        var anonymous = await Client.GetAsync("/auth/revocations");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/auth/revocations?since=0");
        request.Headers.Add(RevocationSyncService.ServiceKeyHeader, AuthStack.FeedKey);
        var feed = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
        Assert.NotNull(await feed.Content.ReadFromJsonAsync<RevocationFeedResponse>());
    }

    [Fact]
    public async Task Jwks_publishes_only_public_key_material()
    {
        var body = await Client.GetFromJsonAsync<JsonElement>("/.well-known/jwks.json");
        var key = Assert.Single(body.GetProperty("keys").EnumerateArray());

        Assert.Equal("RSA", key.GetProperty("kty").GetString());
        Assert.True(key.TryGetProperty("n", out _));
        Assert.False(key.TryGetProperty("d", out _));
        Assert.False(key.TryGetProperty("p", out _));
    }
}
