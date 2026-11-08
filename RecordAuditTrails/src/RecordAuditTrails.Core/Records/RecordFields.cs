using System.Text.Json;
using RecordAuditTrails.Core.Exceptions;

namespace RecordAuditTrails.Core.Records;

/// <summary>Stores record fields as canonical JSON (sorted keys) so equal content always serializes identically.</summary>
public static class RecordFields
{
    public const int MaxFields = 200;
    public const int MaxNameLength = 100;
    public const int MaxValueLength = 10_000;

    public static string Serialize(IReadOnlyDictionary<string, string?> fields)
    {
        var sorted = new SortedDictionary<string, string?>(fields.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal);
        return JsonSerializer.Serialize(sorted);
    }

    public static IReadOnlyDictionary<string, string?> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string?>();
        }
        return JsonSerializer.Deserialize<Dictionary<string, string?>>(json) ?? new Dictionary<string, string?>();
    }

    public static void Validate(IReadOnlyDictionary<string, string?> fields)
    {
        if (fields.Count > MaxFields)
        {
            throw new AuditValidationException($"A record may hold at most {MaxFields} fields.");
        }
        foreach (var (name, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
            {
                throw new AuditValidationException($"Field names must be 1-{MaxNameLength} characters.");
            }
            if (name is RecordFieldNames.Title or RecordFieldNames.RecordType or RecordFieldNames.IsDeleted)
            {
                throw new AuditValidationException($"'{name}' is a reserved field name.");
            }
            if (value is not null && value.Length > MaxValueLength)
            {
                throw new AuditValidationException($"Field '{name}' exceeds {MaxValueLength} characters.");
            }
        }
    }
}

/// <summary>Names used in audit entries for the built-in (non-custom) attributes of a record.</summary>
public static class RecordFieldNames
{
    public const string Title = "Title";
    public const string RecordType = "RecordType";
    public const string IsDeleted = "IsDeleted";
}
