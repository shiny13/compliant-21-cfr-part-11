using RecordAuditTrails.Core.Exceptions;

namespace RecordAuditTrails.Core.Auditing;

/// <summary>Part 11 audit trails must record why a change was made, so a meaningful reason is mandatory.</summary>
public static class ReasonValidator
{
    public const int MinimumLength = 5;
    public const int MaximumLength = 1000;

    public static string Require(string? reason)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length < MinimumLength)
        {
            throw new AuditValidationException($"A reason of at least {MinimumLength} characters is required.");
        }
        if (trimmed.Length > MaximumLength)
        {
            throw new AuditValidationException($"The reason must not exceed {MaximumLength} characters.");
        }
        return trimmed;
    }
}
