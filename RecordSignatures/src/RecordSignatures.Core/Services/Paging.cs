namespace RecordSignatures.Core.Services;

public static class Paging
{
    public const int DefaultTake = 100;
    public const int MaxTake = 500;

    public static (int Skip, int Take) Normalize(int skip, int take) =>
        (Math.Max(skip, 0), take <= 0 ? DefaultTake : Math.Min(take, MaxTake));
}
