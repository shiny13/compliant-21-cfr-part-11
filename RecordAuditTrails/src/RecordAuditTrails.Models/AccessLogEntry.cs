namespace RecordAuditTrails.Models;

/// <summary>Append-only log of who viewed, distributed or exported something (11.10(k)(1) distribution/access control).</summary>
public class AccessLogEntry
{
    public long Id { get; set; }

    /// <summary>"Document" or "Record".</summary>
    public string SubjectType { get; set; } = string.Empty;

    public Guid SubjectId { get; set; }

    public AccessAction Action { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public DateTime TimestampUtc { get; set; }

    /// <summary>Free text such as the distribution recipient or export format.</summary>
    public string? Details { get; set; }
}

public static class AccessSubjects
{
    public const string Document = "Document";
    public const string Record = "Record";
}
