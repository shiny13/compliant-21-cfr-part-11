namespace AuthenticationCore.Service.Auditing;

public interface IAuditLog
{
    void Record(string action, string outcome, string? subject = null, string? detail = null);
}

/// <summary>
/// Emits structured audit events through the logging pipeline. For Part 11 use, route the
/// "AuthenticationCore.Audit" category to a durable, tamper-evident, access-controlled store.
/// </summary>
public sealed class AuditLog(ILoggerFactory loggerFactory) : IAuditLog
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("AuthenticationCore.Audit");

    public void Record(string action, string outcome, string? subject = null, string? detail = null) =>
        _logger.LogInformation(
            "AUDIT action={Action} outcome={Outcome} subject={Subject} detail={Detail}",
            action, outcome, subject ?? "-", detail ?? "-");
}
