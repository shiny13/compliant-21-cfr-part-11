namespace RecordAuditTrails.Services.Records;

public sealed record CreateRecordRequest(
    string RecordType,
    string Title,
    IReadOnlyDictionary<string, string?>? Fields,
    string? Reason);

/// <summary>Null <see cref="Title"/> / <see cref="Fields"/> leave that part unchanged. <see cref="Fields"/> replaces all custom fields.</summary>
public sealed record UpdateRecordRequest(
    string? Title,
    IReadOnlyDictionary<string, string?>? Fields,
    string Reason,
    int? ExpectedVersion);

public sealed record DeleteRecordRequest(string Reason, int? ExpectedVersion);
