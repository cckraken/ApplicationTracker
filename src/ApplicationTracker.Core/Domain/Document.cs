namespace ApplicationTracker.Core.Domain;

/// <summary>What a stored document is. Stored as a string.</summary>
public enum DocumentKind
{
    Cv,
    CoverLetter,
    Other,
}

/// <summary>
/// One version of a CV, cover letter or other file, kept in the document library and linked to the
/// applications it was used for. The file itself lives in the document store under
/// <see cref="StoredFileName"/>; only <c>DocumentService</c> creates, reads or deletes it.
/// </summary>
public class Document
{
    /// <summary>Longest title (version label) accepted.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Largest file accepted, in bytes (10 MB).</summary>
    public const long MaxSizeBytes = 10 * 1024 * 1024;

    public int Id { get; set; }
    public DocumentKind Kind { get; set; }

    /// <summary>The user's version label, e.g. "CV – data roles v3".</summary>
    public required string Title { get; set; }

    /// <summary>The name the file had when it was uploaded; used when downloading it again.</summary>
    public required string OriginalFileName { get; set; }

    /// <summary>The file's name in the document store: a GUID plus the extension, never user input.</summary>
    public required string StoredFileName { get; set; }

    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreatedAt { get; set; }

    public List<JobApplication> Applications { get; } = [];
}
