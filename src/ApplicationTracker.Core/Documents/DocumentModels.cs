using System.ComponentModel.DataAnnotations;
using ApplicationTracker.Core.Domain;

namespace ApplicationTracker.Core.Documents;

/// <summary>What the upload and rename forms collect. Validated by <see cref="DocumentService"/>.</summary>
public class DocumentInput
{
    [Required(ErrorMessage = "Choose what kind of document this is.")]
    [Display(Name = "Kind")]
    public DocumentKind? Kind { get; set; } = DocumentKind.Cv;

    // Trimmed as it is set, so the length limit applies to what is stored (as ContactInput does).
    private string title = "";

    [Required(ErrorMessage = "Give this version a name, like \"CV – data roles v3\".")]
    [StringLength(Document.MaxTitleLength)]
    [Display(Name = "Version name")]
    public string Title
    {
        get => title;
        set => title = value?.Trim() ?? "";
    }

    /// <summary>The title the edit form started from (set by <c>GetForEditAsync</c>); null for uploads.</summary>
    public string? OriginalTitle { get; set; }

    /// <summary>The kind the edit form started from (set by <c>GetForEditAsync</c>).</summary>
    public DocumentKind? OriginalKind { get; set; }
}

/// <summary>What happened to a rename.</summary>
public enum DocumentUpdateResult
{
    Saved,
    NotFound,

    /// <summary>The title or kind was changed elsewhere after the form opened; nothing was saved.</summary>
    ChangedElsewhere,
}

/// <summary>A document as shown in the library and on an application.</summary>
/// <param name="CreatedAt">UTC.</param>
/// <param name="ApplicationCount">How many applications it is linked to.</param>
public sealed record DocumentSummary(
    int Id,
    DocumentKind Kind,
    string Title,
    string OriginalFileName,
    long SizeBytes,
    DateTime CreatedAt,
    int ApplicationCount);

/// <summary>An open document file, ready to send back to the user. The caller disposes <see cref="Content"/>.</summary>
public sealed record DocumentFile(Stream Content, string ContentType, string FileName);
