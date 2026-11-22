namespace ApplicationTracker.Core.Feedback;

/// <summary>
/// Everything needed to ask a chat assistant for feedback on one document for one application:
/// the prompt to paste, and which document to attach with it.
/// </summary>
/// <param name="Prompt">Markdown, with "\n" line endings; the same input always gives the same text.</param>
/// <param name="HasPostingText">False when the application has no job description, so the feedback will be generic.</param>
/// <param name="PackFileName">A safe file name for the zip download, e.g. <c>cv-feedback-contoso-data-analyst.zip</c>.</param>
public sealed record FeedbackPack(
    string Prompt,
    int DocumentId,
    string DocumentTitle,
    string DocumentFileName,
    bool HasPostingText,
    string PackFileName);

/// <summary>A built zip of the prompt and the document. The caller disposes <see cref="Content"/>.</summary>
public sealed record FeedbackPackFile(Stream Content, string FileName);
