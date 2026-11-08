namespace RecordAuditTrails.Core.Auditing;

public sealed record IntegrityResult(bool IsValid, int ItemsChecked, long? FirstBrokenPosition, string? Problem)
{
    public static IntegrityResult Valid(int checkedCount) => new(true, checkedCount, null, null);

    public static IntegrityResult Broken(int checkedCount, long position, string problem) => new(false, checkedCount, position, problem);
}
