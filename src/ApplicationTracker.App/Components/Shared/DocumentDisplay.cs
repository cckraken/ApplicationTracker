using System.Globalization;
using ApplicationTracker.Core.Domain;

namespace ApplicationTracker.App.Components.Shared;

/// <summary>How documents are labelled on screen.</summary>
public static class DocumentDisplay
{
    public static string Label(DocumentKind kind) => kind switch
    {
        DocumentKind.Cv => "CV",
        DocumentKind.CoverLetter => "Cover letter",
        _ => "Other",
    };

    /// <summary>The kinds in the order they are listed, with their plural headings.</summary>
    public static IReadOnlyList<(DocumentKind Kind, string Heading)> Groups { get; } =
    [
        (DocumentKind.Cv, "CVs"),
        (DocumentKind.CoverLetter, "Cover letters"),
        (DocumentKind.Other, "Other documents"),
    ];

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{Math.Ceiling(bytes / 1024.0).ToString("0", CultureInfo.CurrentCulture)} KB",
        _ => $"{(bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.CurrentCulture)} MB",
    };

    public static string FileUrl(int id) => $"documents/{id}/file";
}
