using AuthenticationCore.Shared.Authorization;
using AuthenticationCore.Shared.Claims;
using AuthenticationCore.Shared.InternalContext;
using AuthenticationCore.Shared.Keys;
using AuthenticationCore.Shared.Options;
using AuthenticationCore.Shared.Revocation;
using AuthenticationCore.Shared.Roles;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuthenticationCore.Shared.Validation;

public static class AuthenticationCoreExtensions
{
    public const string BearerScheme = JwtBearerDefaults.AuthenticationScheme;
    public const string InternalContextScheme = "InternalContext";
    public const string CombinedScheme = "AuthenticationCore";

    public static AuthenticationBuilder AddAuthenticationCore(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<AuthenticationCoreOptions>? overrides = null) =>
        services.AddAuthenticationCore(o =>
        {
            configuration.GetSection(AuthenticationCoreOptions.SectionName).Bind(o);
            overrides?.Invoke(o);
        });

    /// <summary>
    /// Stateless JWT validation: signature, issuer, audience and lifetime are checked locally against cached
    /// public keys, and revocation is checked against an in-process cache. No per-request network calls.
    /// </summary>
    public static AuthenticationBuilder AddAuthenticationCore(this IServiceCollection services, Action<AuthenticationCoreOptions> configure)
    {
        services.AddOptions<AuthenticationCoreOptions>()
            .Configure(configure)
            .Validate(o => !string.IsNullOrWhiteSpace(o.PublicKeyPem) || !string.IsNullOrWhiteSpace(o.JwksUrl),
                "Either AuthenticationCore:PublicKeyPem or AuthenticationCore:JwksUrl must be configured.")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IRevocationCache, InMemoryRevocationCache>();
        services.TryAddSingleton<ISigningKeyStore, SigningKeyStore>();
        services.AddSingleton<IAuthorizationHandler, MinimumRoleHandler>();

        var authorization = services.AddAuthorizationBuilder();
        foreach (var role in Enum.GetValues<UserRole>())
        {
            authorization.AddPolicy(RolePolicies.For(role), p => p
                .RequireAuthenticatedUser()
                .AddRequirements(new MinimumRoleRequirement(role)));
        }

        // Both background services exit immediately when their URL is not configured.
        services.AddHttpClient(JwksRefreshService.HttpClientName);
        services.AddHostedService<JwksRefreshService>();
        services.AddHttpClient(RevocationSyncService.HttpClientName);
        services.AddHostedService<RevocationSyncService>();
        services.TryAddSingleton<InternalContextTokenIssuer>();

        var auth = services.AddAuthentication(o =>
        {
            o.DefaultScheme = CombinedScheme;
            o.DefaultChallengeScheme = CombinedScheme;
        });

        auth.AddJwtBearer(BearerScheme, _ => { });
        services.AddOptions<JwtBearerOptions>(BearerScheme)
            .Configure<IOptions<AuthenticationCoreOptions>, ISigningKeyStore, IRevocationCache>(ConfigureBearer);

        auth.AddJwtBearer(InternalContextScheme, _ => { });
        services.AddOptions<JwtBearerOptions>(InternalContextScheme)
            .Configure<IOptions<AuthenticationCoreOptions>>(ConfigureInternalContext);

        // The internal context scheme is only selected when a signing key is configured and the header is present.
        auth.AddPolicyScheme(CombinedScheme, CombinedScheme, o =>
            o.ForwardDefaultSelector = ctx =>
            {
                var internalContext = ctx.RequestServices.GetRequiredService<IOptions<AuthenticationCoreOptions>>().Value.InternalContext;
                return !string.IsNullOrWhiteSpace(internalContext.SigningKey) && ctx.Request.Headers.ContainsKey(internalContext.HeaderName)
                    ? InternalContextScheme
                    : BearerScheme;
            });

        return auth;
    }

    private static void ConfigureBearer(
        JwtBearerOptions jwt,
        IOptions<AuthenticationCoreOptions> options,
        ISigningKeyStore keys,
        IRevocationCache revocations)
    {
        var settings = options.Value;

        jwt.MapInboundClaims = false;
        jwt.SaveToken = false;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            ValidateAudience = true,
            ValidAudience = settings.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = settings.ClockSkew,
            NameClaimType = AuthClaimTypes.Username,
            RoleClaimType = AuthClaimTypes.Role,
            IssuerSigningKeyResolver = (_, _, kid, _) => keys.Resolve(kid),
        };

        jwt.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx =>
            {
                var principal = ctx.Principal!;
                var tokenId = principal.GetTokenId();
                var subject = principal.GetSubject();
                var issuedAt = principal.GetIssuedAt();

                if (tokenId is null || subject is null || issuedAt is null)
                {
                    ctx.Fail("Token is missing required claims.");
                }
                else if (revocations.IsRevoked(tokenId, subject, issuedAt.Value))
                {
                    ctx.Fail("Token has been revoked.");
                }
                return Task.CompletedTask;
            },
        };
    }

    private static void ConfigureInternalContext(JwtBearerOptions jwt, IOptions<AuthenticationCoreOptions> options)
    {
        var settings = options.Value.InternalContext;

        jwt.MapInboundClaims = false;
        jwt.SaveToken = false;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            ValidateAudience = true,
            ValidAudience = settings.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(5),
            IssuerSigningKey = string.IsNullOrWhiteSpace(settings.SigningKey) ? null : InternalContextKeys.Create(settings),
            NameClaimType = AuthClaimTypes.Username,
            RoleClaimType = AuthClaimTypes.Role,
        };

        jwt.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                ctx.Token = ctx.Request.Headers[settings.HeaderName].FirstOrDefault();
                return Task.CompletedTask;
            },
        };
    }
}
