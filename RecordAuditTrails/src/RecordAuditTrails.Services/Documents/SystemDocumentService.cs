using AuthenticationCore.Shared.Roles;
using RecordAuditTrails.Core.Auditing;
using RecordAuditTrails.Core.Exceptions;
using RecordAuditTrails.Core.Hashing;
using RecordAuditTrails.Database;
using RecordAuditTrails.Models;

namespace RecordAuditTrails.Services.Documents;

public interface ISystemDocumentService
{
    Task<DocumentView> CreateAsync(CreateDocumentRequest request, Actor actor, CancellationToken ct = default);

    /// <summary>Adds a new hash-chained revision. Earlier revisions are never changed, so the history stays complete.</summary>
    Task<DocumentView> ReviseAsync(Guid id, ReviseDocumentRequest request, Actor actor, CancellationToken ct = default);

    /// <summary>Documents the actor is permitted to read.</summary>
    Task<IReadOnlyList<DocumentSummary>> ListAsync(Actor actor, CancellationToken ct = default);

    /// <summary>Returns a revision (latest when null). Access is role-checked and logged.</summary>
    Task<DocumentView> GetAsync(Guid id, int? revision, Actor actor, CancellationToken ct = default);

    Task<IReadOnlyList<DocumentRevision>> GetRevisionHistoryAsync(Guid id, Actor actor, CancellationToken ct = default);

    Task<IntegrityResult> VerifyRevisionsAsync(Guid id, CancellationToken ct = default);

    /// <summary>Records that a copy of the document was handed to a recipient.</summary>
    Task<AccessLogEntry> DistributeAsync(Guid id, DistributeDocumentRequest request, Actor actor, CancellationToken ct = default);

    Task<IReadOnlyList<AccessLogEntry>> GetAccessLogAsync(Guid id, int skip, int take, CancellationToken ct = default);
}

public sealed class SystemDocumentService(
    IDocumentRepository documents,
    IAccessLogRepository accessLog,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : ISystemDocumentService
{
    public const string DefaultCreateReason = "Initial release";

    public async Task<DocumentView> CreateAsync(CreateDocumentRequest request, Actor actor, CancellationToken ct = default)
    {
        var number = Require(request.DocumentNumber, "Document number", 100);
        var title = Require(request.Title, "Title", 500);
        var category = Require(request.Category, "Category", 100);
        var content = RequireContent(request.Content);
        if (!UserRoleExtensions.TryParse(request.MinimumReadRole, out var role))
        {
            throw new AuditValidationException($"Minimum read role must be one of: {string.Join(", ", Enum.GetNames<UserRole>())}.");
        }
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? DefaultCreateReason : ReasonValidator.Require(request.Reason);

        if (await documents.NumberExistsAsync(number, ct))
        {
            throw new ConcurrencyConflictException($"Document number '{number}' already exists.");
        }

        var now = TimestampPrecision.ToStorage(clock.GetUtcNow());
        var document = new SystemDocument
        {
            Id = Guid.NewGuid(),
            DocumentNumber = number,
            Title = title,
            Category = category,
            MinimumReadRole = role.ToClaimValue(),
            CurrentRevision = 1,
            CreatedUtc = now,
            CreatedBy = actor.Username,
        };
        var revision = NewRevision(document.Id, 1, title, content, reason, actor, now, CanonicalHasher.GenesisHash);

        documents.Add(document);
        documents.AddRevision(revision);
        await unitOfWork.SaveChangesAsync(ct);
        return new DocumentView(Summarize(document), revision);
    }

    public async Task<DocumentView> ReviseAsync(Guid id, ReviseDocumentRequest request, Actor actor, CancellationToken ct = default)
    {
        var content = RequireContent(request.Content);
        var reason = ReasonValidator.Require(request.ChangeDescription);
        var document = await GetDocumentAsync(id, ct);
        if (request.ExpectedRevision is { } expected && expected != document.CurrentRevision)
        {
            throw new ConcurrencyConflictException($"The document is at revision {document.CurrentRevision}, not {expected}. Reload and retry.");
        }

        var latest = await documents.GetLatestRevisionAsync(id, ct)
            ?? throw new NotFoundException($"Document {id} has no revisions.");
        var title = request.Title is null ? document.Title : Require(request.Title, "Title", 500);
        if (content == latest.Content && title == latest.Title)
        {
            throw new AuditValidationException("The revision does not change the document.");
        }

        var now = TimestampPrecision.ToStorage(clock.GetUtcNow());
        var revision = NewRevision(id, latest.RevisionNumber + 1, title, content, reason, actor, now, latest.Hash);
        document.Title = title;
        document.CurrentRevision = revision.RevisionNumber;

        documents.AddRevision(revision);
        await unitOfWork.SaveChangesAsync(ct);
        return new DocumentView(Summarize(document), revision);
    }

    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(Actor actor, CancellationToken ct = default) =>
        (await documents.ListAsync(ct)).Where(d => CanRead(d, actor)).Select(Summarize).ToList();

    public async Task<DocumentView> GetAsync(Guid id, int? revision, Actor actor, CancellationToken ct = default)
    {
        var document = await GetReadableAsync(id, actor, ct);
        var revisions = await documents.GetRevisionsAsync(id, ct);
        var wanted = revision ?? document.CurrentRevision;
        var found = revisions.FirstOrDefault(r => r.RevisionNumber == wanted)
            ?? throw new NotFoundException($"Revision {wanted} of document {id} was not found.");

        Log(document.Id, AccessAction.View, actor, $"revision {wanted}");
        await unitOfWork.SaveChangesAsync(ct);
        return new DocumentView(Summarize(document), found);
    }

    public async Task<IReadOnlyList<DocumentRevision>> GetRevisionHistoryAsync(Guid id, Actor actor, CancellationToken ct = default)
    {
        var document = await GetReadableAsync(id, actor, ct);
        Log(document.Id, AccessAction.View, actor, "revision history");
        await unitOfWork.SaveChangesAsync(ct);
        return await documents.GetRevisionsAsync(id, ct);
    }

    public async Task<IntegrityResult> VerifyRevisionsAsync(Guid id, CancellationToken ct = default)
    {
        await GetDocumentAsync(id, ct);
        return ChainVerifier.VerifyRevisions(await documents.GetRevisionsAsync(id, ct));
    }

    public async Task<AccessLogEntry> DistributeAsync(Guid id, DistributeDocumentRequest request, Actor actor, CancellationToken ct = default)
    {
        var document = await GetReadableAsync(id, actor, ct);
        var recipient = Require(request.Recipient, "Recipient", 200);
        var details = string.IsNullOrWhiteSpace(request.Note)
            ? $"revision {document.CurrentRevision} to {recipient}"
            : $"revision {document.CurrentRevision} to {recipient}: {request.Note.Trim()}";
        if (details.Length > 1000)
        {
            throw new AuditValidationException("Distribution details must not exceed 1000 characters.");
        }

        var entry = Log(document.Id, AccessAction.Distribute, actor, details);
        await unitOfWork.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<IReadOnlyList<AccessLogEntry>> GetAccessLogAsync(Guid id, int skip, int take, CancellationToken ct = default)
    {
        await GetDocumentAsync(id, ct);
        var (s, t) = Paging.Normalize(skip, take);
        return await accessLog.ListAsync(AccessSubjects.Document, id, s, t, ct);
    }

    private async Task<SystemDocument> GetDocumentAsync(Guid id, CancellationToken ct) =>
        await documents.GetAsync(id, ct) ?? throw new NotFoundException($"Document {id} was not found.");

    private async Task<SystemDocument> GetReadableAsync(Guid id, Actor actor, CancellationToken ct)
    {
        var document = await GetDocumentAsync(id, ct);
        if (!CanRead(document, actor))
        {
            throw new AccessDeniedException("Your role is not permitted to access this document.");
        }
        return document;
    }

    private static bool CanRead(SystemDocument document, Actor actor) =>
        !UserRoleExtensions.TryParse(document.MinimumReadRole, out var minimum) || actor.Role.Satisfies(minimum);

    private AccessLogEntry Log(Guid documentId, AccessAction action, Actor actor, string details)
    {
        var entry = new AccessLogEntry
        {
            SubjectType = AccessSubjects.Document,
            SubjectId = documentId,
            Action = action,
            UserId = actor.UserId,
            Username = actor.Username,
            Role = actor.Role.ToString(),
            TimestampUtc = TimestampPrecision.ToStorage(clock.GetUtcNow()),
            Details = details,
        };
        accessLog.Add(entry);
        return entry;
    }

    private static DocumentRevision NewRevision(
        Guid documentId, int number, string title, string content, string reason, Actor actor, DateTime now, string previousHash)
    {
        var revision = new DocumentRevision
        {
            DocumentId = documentId,
            RevisionNumber = number,
            Title = title,
            Content = content,
            ChangeDescription = reason,
            AuthorId = actor.UserId,
            Author = actor.Username,
            CreatedUtc = now,
            PreviousHash = previousHash,
        };
        revision.Hash = AuditHash.ForRevision(revision);
        return revision;
    }

    private static DocumentSummary Summarize(SystemDocument d) =>
        new(d.Id, d.DocumentNumber, d.Title, d.Category, d.MinimumReadRole, d.CurrentRevision, d.CreatedUtc, d.CreatedBy);

    private static string Require(string? value, string name, int max)
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

    private static string RequireContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new AuditValidationException("Content is required.");
        }
        if (content.Length > 1_000_000)
        {
            throw new AuditValidationException("Content must not exceed 1,000,000 characters.");
        }
        return content;
    }
}
