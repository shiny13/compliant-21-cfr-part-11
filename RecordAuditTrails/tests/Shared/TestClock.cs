namespace RecordAuditTrails.TestSupport;

/// <summary>Controllable clock so audit timestamps are deterministic in tests.</summary>
public sealed class TestClock(DateTimeOffset? start = null) : TimeProvider
{
    private DateTimeOffset _now = start ?? new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
