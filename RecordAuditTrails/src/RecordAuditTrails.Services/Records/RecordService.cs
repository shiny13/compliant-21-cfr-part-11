using RecordAuditTrails.Core.Auditing;
using RecordAuditTrails.Core.Exceptions;
using RecordAuditTrails.Core.Records;
using RecordAuditTrails.Core.Retention;
using RecordAuditTrails.Core.Hashing;
using RecordAuditTrails.Database;
using RecordAuditTrails.Models;
using RecordAuditTrails.Services.Auditing;

namespace RecordAuditTrails.Services.Records;

public interface IRecordService
{
    Task<RecordCopy> CreateAsync(CreateRecordRequest request, Actor actor, CancellationToken ct = default);

    Task<RecordCopy> UpdateAsync(Guid id, UpdateRecordRequest request, Actor actor, CancellationToken ct = default);

    /// <summary>Logical delete only: the record stays, flagged as deleted, with its full history.</summary>
    Task<RecordCopy> DeleteAsync(Guid id, DeleteRecordRequest request, Actor actor, CancellationToken ct = default);

    Task<RecordCopy> GetAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<RecordCopy>> ListAsync(string? recordType, bool includeDeleted, int skip, int take, CancellationToken ct = default);
}

public sealed class RecordService(
    IRecordRepository records,
    IAuditTrailWriter writer,
    IUnitOfWork unitOfWork,
    RetentionPolicy retention,
    TimeProvider clock) : IRecordService
{
    public const string DefaultCreateReason = "Record created";
    private const int MaxTitleLength = 500;
    private const int MaxTypeLength = 100;

    public async Task<RecordCopy> CreateAsync(CreateRecordRequest request, Actor actor, CancellationToken ct = default)
    {
        var recordType = RequireText(request.RecordType, "Record type", MaxTypeLength);
        var title = RequireText(request.Title, "Title", MaxTitleLength);
        var fields = request.Fields ?? new Dictionary<string, string?>();
        RecordFields.Validate(fields);
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? DefaultCreateReason : ReasonValidator.Require(request.Reason);

        var now = TimestampPrecision.ToStorage(clock.GetUtcNow());
        var record = new ElectronicRecord
        {
            Id = Guid.NewGuid(),
            RecordType = recordType,
            Title = title,
            DataJson = RecordFields.Serialize(fields),
            Version = 1,
            RetainUntilUtc = retention.RetainUntil(recordType, now),
            CreatedUtc = now,
            CreatedBy = actor.Username,
            ModifiedUtc = now,
            ModifiedBy = actor.Username,
        };

        var after = Flatten(record.RecordType, record.Title, fields);
        var changes = ChangeDetector.Detect(new Dictionary<string, string?>(), after);

        records.Add(record);
        await writer.AppendAsync(record.Id, record.Version, AuditAction.Create, changes, reason, actor, now, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return RecordCopy.From(record);
    }

    public async Task<RecordCopy> UpdateAsync(Guid id, UpdateRecordRequest request, Actor actor, CancellationToken ct = default)
    {
        var reason = ReasonValidator.Require(request.Reason);
        var record = await GetTrackedAsync(id, ct);
        EnsureNotDeleted(record);
        EnsureExpectedVersion(record, request.ExpectedVersion);

        var before = Flatten(record.RecordType, record.Title, RecordFields.Deserialize(record.DataJson));

        var title = request.Title is null ? record.Title : RequireText(request.Title, "Title", MaxTitleLength);
        var fields = request.Fields ?? RecordFields.Deserialize(record.DataJson);
        RecordFields.Validate(fields);

        var changes = ChangeDetector.Detect(before, Flatten(record.RecordType, title, fields));
        if (changes.Count == 0)
        {
            throw new AuditValidationException("The update does not change anything.");
        }

        var now = TimestampPrecision.ToStorage(clock.GetUtcNow());
        record.Title = title;
        record.DataJson = RecordFields.Serialize(fields);
        record.Version++;
        record.ModifiedUtc = now;
        record.ModifiedBy = actor.Username;

        await writer.AppendAsync(record.Id, record.Version, AuditAction.Modify, changes, reason, actor, now, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return RecordCopy.From(record);
    }

    public async Task<RecordCopy> DeleteAsync(Guid id, DeleteRecordRequest request, Actor actor, CancellationToken ct = default)
    {
        var reason = ReasonValidator.Require(request.Reason);
        var record = await GetTrackedAsync(id, ct);
        EnsureNotDeleted(record);
        EnsureExpectedVersion(record, request.ExpectedVersion);

        var now = TimestampPrecision.ToStorage(clock.GetUtcNow());
        record.IsDeleted = true;
        record.Version++;
        record.ModifiedUtc = now;
        record.ModifiedBy = actor.Username;

        var changes = new[] { new FieldChange(RecordFieldNames.IsDeleted, "false", "true") };
        await writer.AppendAsync(record.Id, record.Version, AuditAction.Delete, changes, reason, actor, now, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return RecordCopy.From(record);
    }

    public async Task<RecordCopy> GetAsync(Guid id, CancellationToken ct = default) =>
        RecordCopy.From(await GetTrackedAsync(id, ct));

    public async Task<IReadOnlyList<RecordCopy>> ListAsync(string? recordType, bool includeDeleted, int skip, int take, CancellationToken ct = default)
    {
        var (s, t) = Paging.Normalize(skip, take);
        return (await records.ListAsync(recordType, includeDeleted, s, t, ct)).Select(RecordCopy.From).ToList();
    }

    private async Task<ElectronicRecord> GetTrackedAsync(Guid id, CancellationToken ct) =>
        await records.GetAsync(id, ct) ?? throw new NotFoundException($"Record {id} was not found.");

    private static void EnsureNotDeleted(ElectronicRecord record)
    {
        if (record.IsDeleted)
        {
            throw new AuditValidationException("The record has been deleted and can no longer be changed.");
        }
    }

    private static void EnsureExpectedVersion(ElectronicRecord record, int? expected)
    {
        if (expected is { } version && version != record.Version)
        {
            throw new ConcurrencyConflictException($"The record is at version {record.Version}, not {version}. Reload and retry.");
        }
    }

    private static string RequireText(string? value, string name, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            throw new AuditValidationException($"{name} is required.");
        }
        if (trimmed.Length > max)
        {
            throw new AuditValidationException($"{name} must not exceed {max} characters.");
        }
        return trimmed;
    }

    private static Dictionary<string, string?> Flatten(string recordType, string title, IReadOnlyDictionary<string, string?> fields)
    {
        var all = new Dictionary<string, string?>(fields, StringComparer.Ordinal)
        {
            [RecordFieldNames.RecordType] = recordType,
            [RecordFieldNames.Title] = title,
        };
        return all;
    }
}
