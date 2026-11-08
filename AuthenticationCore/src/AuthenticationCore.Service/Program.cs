using AuthenticationCore.Service.Auditing;
using AuthenticationCore.Service.Endpoints;
using AuthenticationCore.Service.Options;
using AuthenticationCore.Service.Revocation;
using AuthenticationCore.Service.Security;
using AuthenticationCore.Service.Tokens;
using AuthenticationCore.Service.Users;
using AuthenticationCore.Shared.Options;
using AuthenticationCore.Shared.Validation;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AuthServiceOptions>(builder.Configuration.GetSection(AuthServiceOptions.SectionName));
builder.Services.AddSingleton(sp =>
    ServiceSigningKey.LoadOrCreate(sp.GetRequiredService<IOptions<AuthServiceOptions>>().Value.SigningKeyPemPath));

// The service validates its own tokens (/auth/me, /auth/logout) through the same shared library as every other API.
builder.Services.AddAuthenticationCore(builder.Configuration);
builder.Services.AddOptions<AuthenticationCoreOptions>()
    .Configure<ServiceSigningKey>((options, key) => options.PublicKeyPem = key.PublicKeyPem);

builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<IUserStore, InMemoryUserStore>();
builder.Services.AddSingleton<UserAuthenticator>();
builder.Services.AddSingleton<AccessTokenIssuer>();
builder.Services.AddSingleton<RefreshTokenService>();
builder.Services.AddSingleton<RevocationLog>();
builder.Services.AddSingleton<RevocationService>();
builder.Services.AddSingleton<IAuditLog, AuditLog>();

var app = builder.Build();

if (app.Services.GetRequiredService<ServiceSigningKey>().Ephemeral)
{
    app.Logger.LogWarning("No AuthService:SigningKeyPemPath configured; using an ephemeral signing key. Tokens become invalid on restart.");
}

await UserSeeder.SeedAsync(
    app.Services.GetRequiredService<IUserStore>(),
    app.Services.GetRequiredService<PasswordHasher>(),
    app.Services.GetRequiredService<IOptions<AuthServiceOptions>>().Value);

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();

app.Run();

namespace AuthenticationCore.Service
{
    /// <summary>Entry point marker for integration tests.</summary>
    public sealed class ServiceMarker;
}
