namespace RecordAuditTrails.Models;

/// <summary>System operation / maintenance documentation (SOPs, manuals) under revision control.</summary>
public class SystemDocument
{
    public Guid Id { get; set; }

    public string DocumentNumber { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    /// <summary>Lowest role allowed to read the document (an AuthenticationCore role name).</summary>
    public string MinimumReadRole { get; set; } = string.Empty;

    public int CurrentRevision { get; set; }

    public DateTime CreatedUtc { get; set; }

    public string CreatedBy { get; set; } = string.Empty;

    public List<DocumentRevision> Revisions { get; set; } = [];
}

/// <summary>One immutable, hash-chained revision of a <see cref="SystemDocument"/> with its change description.</summary>
public class DocumentRevision
{
    public long Id { get; set; }

    public Guid DocumentId { get; set; }

    public int RevisionNumber { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    /// <summary>Why the document was changed.</summary>
    public string ChangeDescription { get; set; } = string.Empty;

    public string AuthorId { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public string PreviousHash { get; set; } = string.Empty;

    public string Hash { get; set; } = string.Empty;
}
