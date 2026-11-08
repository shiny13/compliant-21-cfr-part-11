using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RecordSignatures.Core.Abstractions;
using RecordSignatures.Core.Integration;
using RecordSignatures.Core.Models;
using RecordSignatures.Core.Sealing;
using RecordSignatures.Core.Services;

namespace RecordSignatures.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the business services, the integration clients and the alert sinks.</summary>
    public static IServiceCollection AddRecordSignatures(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SignatureOptions>(configuration.GetSection(SignatureOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SignatureSealer>();

        services.AddHttpClient<AuthenticationCoreClient>(ConfigureClient);
        services.AddTransient<ICredentialVerifier>(sp => sp.GetRequiredService<AuthenticationCoreClient>());
        services.AddTransient<ISessionRevoker>(sp => sp.GetRequiredService<AuthenticationCoreClient>());
        services.AddHttpClient<IAuditTrailClient, AuditTrailClient>(ConfigureClient);
        services.AddHttpClient<WebhookAlertSink>(ConfigureClient);

        services.AddTransient<ISecurityAlertSink, LoggingAlertSink>();
        services.AddTransient<ISecurityAlertSink, AuditTrailAlertSink>();
        services.AddTransient<ISecurityAlertSink>(sp =>
            sp.GetRequiredService<WebhookAlertSink>());

        services.AddScoped<ISecurityAlertService, SecurityAlertService>();
        services.AddScoped<ICredentialChallenge, CredentialChallenge>();
        services.AddScoped<ISignerService, SignerService>();
        services.AddScoped<ISignatureService, SignatureService>();
        services.AddScoped<ISignatureReportService, SignatureReportService>();
        return services;
    }

    private static void ConfigureClient(IServiceProvider sp, HttpClient client) =>
        client.Timeout = sp.GetRequiredService<IOptions<SignatureOptions>>().Value.HttpTimeout;
}
