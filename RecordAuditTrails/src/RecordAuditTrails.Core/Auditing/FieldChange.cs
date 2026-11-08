namespace RecordAuditTrails.Core.Auditing;

public sealed record FieldChange(string Field, string? OldValue, string? NewValue);

/// <summary>Finds which fields differ between two states, so every change is recorded with old and new value.</summary>
public static class ChangeDetector
{
    public static IReadOnlyList<FieldChange> Detect(
        IReadOnlyDictionary<string, string?> before,
        IReadOnlyDictionary<string, string?> after)
    {
        var changes = new List<FieldChange>();
        foreach (var key in before.Keys.Union(after.Keys).Order(StringComparer.Ordinal))
        {
            before.TryGetValue(key, out var oldValue);
            after.TryGetValue(key, out var newValue);
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                changes.Add(new FieldChange(key, oldValue, newValue));
            }
        }
        return changes;
    }
}
