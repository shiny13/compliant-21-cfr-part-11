using RecordAuditTrails.Models;

namespace RecordAuditTrails.Services.Documents;

public sealed record CreateDocumentRequest(
    string DocumentNumber,
    string Title,
    string Category,
    string Content,
    string MinimumReadRole,
    string? Reason);

public sealed record ReviseDocumentRequest(string? Title, string Content, string ChangeDescription, int? ExpectedRevision);

public sealed record DistributeDocumentRequest(string Recipient, string? Note);

public sealed record DocumentSummary(
    Guid Id, string DocumentNumber, string Title, string Category, string MinimumReadRole, int CurrentRevision, DateTime CreatedUtc, string CreatedBy);

public sealed record DocumentView(DocumentSummary Summary, DocumentRevision Revision);
