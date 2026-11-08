using System.Net.Http.Json;
using AuthenticationCore.Shared.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthenticationCore.Shared.Revocation;

/// <summary>
/// Pulls revocation entries from the auth service in the background and applies them to the local cache.
/// Request-time checks only read the cache. If the feed is unreachable the last known state is kept and
/// the short access token TTL bounds how long a missed revocation can matter.
/// </summary>
public sealed class RevocationSyncService(
    IHttpClientFactory httpClientFactory,
    IRevocationCache cache,
    IOptions<AuthenticationCoreOptions> options,
    ILogger<RevocationSyncService> logger) : BackgroundService
{
    public const string HttpClientName = "AuthenticationCore.Revocation";
    public const string ServiceKeyHeader = "X-Service-Key";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var feed = options.Value.Revocation;
        if (string.IsNullOrWhiteSpace(feed.FeedUrl))
        {
            return;
        }

        long since = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                since = await SyncOnceAsync(feed, since, stoppingToken);
                cache.PruneExpired();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Revocation feed poll failed; using last known revocation state.");
            }

            try
            {
                await Task.Delay(feed.PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<long> SyncOnceAsync(RevocationFeedOptions feed, long since, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{feed.FeedUrl}?since={since}");
        if (!string.IsNullOrEmpty(feed.FeedServiceKey))
        {
            request.Headers.Add(ServiceKeyHeader, feed.FeedServiceKey);
        }

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<RevocationFeedResponse>(ct)
            ?? throw new InvalidOperationException("Empty revocation feed response.");

        // The auth service restarted and its sequence went backwards: start over from the beginning.
        if (body.Latest < since)
        {
            return 0;
        }

        foreach (var entry in body.Entries)
        {
            cache.Apply(entry);
        }
        return body.Latest;
    }
}
