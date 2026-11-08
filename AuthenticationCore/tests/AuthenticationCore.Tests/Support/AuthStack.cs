using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthenticationCore.SampleApi;
using AuthenticationCore.Service;
using AuthenticationCore.Service.Endpoints;
using AuthenticationCore.Shared.Keys;
using AuthenticationCore.Shared.Revocation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AuthenticationCore.Tests.Support;

/// <summary>
/// Runs the real auth service and a real sample API in memory. The API learns keys and revocations from the
/// service the same way it would in production: background fetches, never per request.
/// </summary>
public sealed class AuthStack : IAsyncLifetime
{
    public const string FeedKey = "test-feed-key";
    public const string InternalContextKey = "dGVzdC1pbnRlcm5hbC1jb250ZXh0LWtleS0zMi1ieXRlcyE=";

    private static readonly (string Name, string Role)[] Users =
    [
        ("admin", "Administrator"),
        ("manager", "LabManager"),
        ("reviewer", "Reviewer"),
        ("technician", "LabTechnician"),
        ("logout-user", "LabTechnician"),
        ("refresh-user", "LabTechnician"),
        ("lockout-user", "LabTechnician"),
        ("revoke-target", "LabTechnician"),
        ("propagation-user", "LabTechnician"),
    ];

    private readonly CountingHandler _jwksCalls = new();

    public WebApplicationFactory<ServiceMarker> Service { get; private set; } = null!;

    public WebApplicationFactory<SampleApiMarker> Sample { get; private set; } = null!;

    public int JwksRequestCount => _jwksCalls.Count;

    public static string PasswordFor(string user) => $"Test-{user}-Passw0rd!";

    public async Task InitializeAsync()
    {
        Service = new WebApplicationFactory<ServiceMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                var settings = new Dictionary<string, string?>
                {
                    ["AuthService:RevocationFeedServiceKey"] = FeedKey,
                    ["AuthService:MaxFailedAttempts"] = "3",
                };
                for (var i = 0; i < Users.Length; i++)
                {
                    settings[$"AuthService:SeedUsers:{i}:Username"] = Users[i].Name;
                    settings[$"AuthService:SeedUsers:{i}:Password"] = PasswordFor(Users[i].Name);
                    settings[$"AuthService:SeedUsers:{i}:Roles:0"] = Users[i].Role;
                }
                config.AddInMemoryCollection(settings);
            });
        });

        _ = Service.Server;
        _jwksCalls.InnerHandler = Service.Server.CreateHandler();

        Sample = new WebApplicationFactory<SampleApiMarker>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AuthenticationCore:JwksUrl"] = "http://auth.test/.well-known/jwks.json",
                ["AuthenticationCore:Revocation:FeedUrl"] = "http://auth.test/auth/revocations",
                ["AuthenticationCore:Revocation:FeedServiceKey"] = FeedKey,
                ["AuthenticationCore:Revocation:PollInterval"] = "00:00:00.200",
                ["AuthenticationCore:InternalContext:SigningKey"] = InternalContextKey,
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient(JwksRefreshService.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => _jwksCalls);
                services.AddHttpClient(RevocationSyncService.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => Service.Server.CreateHandler());
            });
        });

        var keys = Sample.Services.GetRequiredService<ISigningKeyStore>();
        await WaitUntilAsync(() => keys.HasKeys);
    }

    public Task DisposeAsync()
    {
        Sample.Dispose();
        Service.Dispose();
        return Task.CompletedTask;
    }

    public async Task<TokenResponse> LoginAsync(string user)
    {
        var response = await Service.CreateClient().PostAsJsonAsync("/auth/login", new LoginRequest(user, PasswordFor(user)));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>())!;
    }

    public static HttpRequestMessage Get(string url, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    public static HttpRequestMessage Post(string url, string accessToken, object? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return request;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }
            await Task.Delay(50);
        }
    }

    private sealed class CountingHandler : DelegatingHandler
    {
        private int _count;

        public int Count => _count;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
