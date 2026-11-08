namespace RecordAuditTrails.Core.Retention;

public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>Years a record and its audit trail are retained when its type has no specific rule.</summary>
    public int DefaultYears { get; set; } = 7;

    /// <summary>Per record type retention in years, e.g. clinical records may require longer than manufacturing records.</summary>
    public Dictionary<string, int> ByRecordType { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Audit trails must be kept at least as long as the records they describe (11.10(e)). The audit trail of a record
/// therefore always inherits the record's retain-until date.
/// </summary>
public sealed class RetentionPolicy(RetentionOptions options)
{
    public int YearsFor(string recordType) =>
        options.ByRecordType.TryGetValue(recordType, out var years) && years > 0 ? years : Math.Max(options.DefaultYears, 1);

    public DateTime RetainUntil(string recordType, DateTime createdUtc) => createdUtc.AddYears(YearsFor(recordType));

    public static bool IsExpired(DateTime retainUntilUtc, DateTime nowUtc) => nowUtc >= retainUntilUtc;
}
