namespace RecordSignatures.Core.Sealing;

/// <summary>PostgreSQL stores microseconds; truncating up front keeps hashed timestamps identical after a round trip.</summary>
public static class TimestampPrecision
{
    private const long TicksPerMicrosecond = 10;

    public static DateTime ToStorage(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        return new DateTime(utc.Ticks - utc.Ticks % TicksPerMicrosecond, DateTimeKind.Utc);
    }

    /// <summary>The canonical text used when a timestamp is hashed.</summary>
    public static string Canonical(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", System.Globalization.CultureInfo.InvariantCulture);
}
