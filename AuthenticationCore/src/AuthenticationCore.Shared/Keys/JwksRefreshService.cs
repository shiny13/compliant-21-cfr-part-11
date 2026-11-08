using AuthenticationCore.Shared.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuthenticationCore.Shared.Keys;

/// <summary>Refreshes the trusted key set in the background so requests never wait on the auth service.</summary>
public sealed class JwksRefreshService(
    IHttpClientFactory httpClientFactory,
    ISigningKeyStore store,
    IOptions<AuthenticationCoreOptions> options,
    ILogger<JwksRefreshService> logger) : BackgroundService
{
    public const string HttpClientName = "AuthenticationCore.Jwks";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.JwksUrl))
        {
            return;
        }

        var retry = TimeSpan.FromSeconds(2);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = settings.JwksRefreshInterval;
            try
            {
                using var client = httpClientFactory.CreateClient(HttpClientName);
                var json = await client.GetStringAsync(settings.JwksUrl, stoppingToken);
                store.Replace(new JsonWebKeySet(json).GetSigningKeys());
                retry = TimeSpan.FromSeconds(2);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "JWKS refresh failed; keeping previously cached keys.");
                delay = retry < settings.JwksRefreshInterval ? retry : settings.JwksRefreshInterval;
                retry = TimeSpan.FromSeconds(Math.Min(retry.TotalSeconds * 2, 60));
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
