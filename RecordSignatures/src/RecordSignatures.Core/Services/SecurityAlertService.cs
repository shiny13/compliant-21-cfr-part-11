using Microsoft.Extensions.Logging;
using RecordSignatures.Core.Abstractions;
using RecordSignatures.Core.Models;
using RecordSignatures.Core.Sealing;

namespace RecordSignatures.Core.Services;

public interface ISecurityAlertService
{
    /// <summary>Stores the alert, then reports it to every sink at once. Sink failures never propagate.</summary>
    Task<SecurityAlert> RaiseAsync(AlertType type, AlertSeverity severity, string? userId, string? username, string detail, Operator? caller, CancellationToken ct = default);

    Task<IReadOnlyList<SecurityAlert>> ListAsync(AlertSeverity? minimumSeverity, DateTime? fromUtc, int skip, int take, CancellationToken ct = default);
}

/// <summary>
/// Detects and reports attempted unauthorized use of identification codes and passwords "in an immediate and urgent
/// manner" (11.300(d)): the alert is stored first, so it can never be lost to a sink failure, and is then pushed to
/// the log, the audit trail and any configured webhook.
/// </summary>
public sealed class SecurityAlertService(
    ISecurityStore store,
    IEnumerable<ISecurityAlertSink> sinks,
    TimeProvider clock,
    ILogger<SecurityAlertService> logger) : ISecurityAlertService
{
    public async Task<SecurityAlert> RaiseAsync(
        AlertType type, AlertSeverity severity, string? userId, string? username, string detail, Operator? caller, CancellationToken ct = default)
    {
        var alert = new SecurityAlert(Guid.NewGuid(), TimestampPrecision.ToStorage(clock.GetUtcNow()), severity, type, userId, username, detail, null);
        await store.AddAlertAsync(alert, ct);

        foreach (var sink in sinks)
        {
            try
            {
                await sink.PublishAsync(alert, caller, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Security alert sink {Sink} failed for alert {AlertId}", sink.GetType().Name, alert.Id);
            }
        }
        return alert;
    }

    public Task<IReadOnlyList<SecurityAlert>> ListAsync(AlertSeverity? minimumSeverity, DateTime? fromUtc, int skip, int take, CancellationToken ct = default)
    {
        var (s, t) = Paging.Normalize(skip, take);
        return store.ListAlertsAsync(minimumSeverity, fromUtc, s, t, ct);
    }
}
