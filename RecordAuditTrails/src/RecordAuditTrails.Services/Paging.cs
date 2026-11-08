namespace RecordAuditTrails.Services;

public static class Paging
{
    public const int MaxPageSize = 500;
    public const int DefaultPageSize = 100;

    public static (int Skip, int Take) Normalize(int skip, int take) =>
        (Math.Max(skip, 0), take <= 0 ? DefaultPageSize : Math.Min(take, MaxPageSize));
}
