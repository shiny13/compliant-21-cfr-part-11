namespace RecordSignatures.Core.Models;

public sealed class SignatureOptions
{
    public const string SectionName = "Signatures";

    /// <summary>Consecutive failed credential checks that lock an individual's signing (11.300(d)).</summary>
    public int MaxFailedAttempts { get; set; } = 3;

    /// <summary>Only failures within this window count towards the lockout.</summary>
    public TimeSpan FailureWindow { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How often each individual's signing authority must be re-verified by an administrator (11.300(b)).</summary>
    public int ReviewIntervalMonths { get; set; } = 12;

    public int MaxCommentLength { get; set; } = 500;

    public int MinReasonLength { get; set; } = 5;

    /// <summary>HMAC keys that seal signatures: key id to base64 key (at least 32 bytes). Keep old keys so old signatures still verify.</summary>
    public Dictionary<string, string> SealKeys { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The key id used to seal new signatures.</summary>
    public string? ActiveSealKeyId { get; set; }

    /// <summary>Base URL of the AuthenticationCore service, used to verify signing credentials and to revoke sessions.</summary>
    public string? AuthServiceBaseUrl { get; set; }

    /// <summary>Base URL of the RecordAuditTrails API that holds records and the audit trail.</summary>
    public string? AuditTrailBaseUrl { get; set; }

    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Optional webhook that receives every security alert immediately (e.g. a security operations channel).</summary>
    public string? AlertWebhookUrl { get; set; }

    public string OrganizationName { get; set; } = "The organization";
}
