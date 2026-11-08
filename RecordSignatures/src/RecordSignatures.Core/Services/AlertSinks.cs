using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RecordSignatures.Core.Abstractions;
using RecordSignatures.Core.Models;

namespace RecordSignatures.Core.Services;

/// <summary>Writes every alert to the application log at a level matching its severity, for log-based monitoring.</summary>
public sealed class LoggingAlertSink(ILogger<LoggingAlertSink> logger) : ISecurityAlertSink
{
    public Task PublishAsync(SecurityAlert alert, Operator? caller, CancellationToken ct = default)
    {
        var level = alert.Severity switch
        {
            AlertSeverity.Critical => LogLevel.Critical,
            AlertSeverity.Warning => LogLevel.Warning,
            _ => LogLevel.Information,
        };
        logger.Log(level, "SECURITY-ALERT {Type} severity={Severity} user={Username} ({UserId}) alert={AlertId}: {Detail}",
            alert.Type, alert.Severity, alert.Username, alert.UserId, alert.Id, alert.Detail);
        return Task.CompletedTask;
    }
}

/// <summary>Records the alert in the RecordAuditTrails audit trail, using the caller's token when there is one.</summary>
public sealed class AuditTrailAlertSink(IAuditTrailClient audit) : ISecurityAlertSink
{
    public async Task PublishAsync(SecurityAlert alert, Operator? caller, CancellationToken ct = default)
    {
        if (caller is null)
        {
            return;
        }
        await audit.CreateRecordAsync(
            AuditFields.AlertRecordType, $"{alert.Type}: {alert.Username ?? "unknown user"}", AuditFields.ForAlert(alert),
            $"Security alert raised: {alert.Type}", caller.AccessToken, ct);
    }
}

/// <summary>Posts the alert as JSON to <see cref="SignatureOptions.AlertWebhookUrl"/> so the security unit is told immediately.</summary>
public sealed class WebhookAlertSink(HttpClient http, IOptions<SignatureOptions> options) : ISecurityAlertSink
{
    public async Task PublishAsync(SecurityAlert alert, Operator? caller, CancellationToken ct = default)
    {
        var url = options.Value.AlertWebhookUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }
        using var response = await http.PostAsJsonAsync(url, new
        {
            alert.Id,
            alert.RaisedUtc,
            Severity = alert.Severity.ToString(),
            Type = alert.Type.ToString(),
            alert.UserId,
            alert.Username,
            alert.Detail,
        }, ct);
        response.EnsureSuccessStatusCode();
    }
}
