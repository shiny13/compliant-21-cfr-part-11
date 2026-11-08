using System.Globalization;

namespace RecordAuditTrails.Core.Hashing;

/// <summary>
/// PostgreSQL stores microsecond precision while .NET holds 100 ns ticks. Timestamps are truncated before they are
/// hashed and stored so the hash recomputed from a database round trip always matches.
/// </summary>
public static class TimestampPrecision
{
    public static DateTime ToStorage(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }

    public static string Format(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
}
