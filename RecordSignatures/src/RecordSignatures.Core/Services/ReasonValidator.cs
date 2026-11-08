using RecordSignatures.Core.Exceptions;

namespace RecordSignatures.Core.Services;

internal static class Validation
{
    public static string RequireText(string? value, string name, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new SignatureValidationException($"{name} is required.");
        }
        if (trimmed.Length > max)
        {
            throw new SignatureValidationException($"{name} must not exceed {max} characters.");
        }
        return trimmed;
    }

    public static string RequireReason(string? value, int minLength)
    {
        var trimmed = value?.Trim();
        if (trimmed is null || trimmed.Length < minLength)
        {
            throw new SignatureValidationException($"A reason of at least {minLength} characters is required.");
        }
        if (trimmed.Length > 1000)
        {
            throw new SignatureValidationException("The reason must not exceed 1000 characters.");
        }
        return trimmed;
    }
}
