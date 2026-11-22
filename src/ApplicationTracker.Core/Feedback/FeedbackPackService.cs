using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ApplicationTracker.Core.Data;
using ApplicationTracker.Core.Documents;
using ApplicationTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace ApplicationTracker.Core.Feedback;

/// <summary>
/// Builds a CV or cover-letter feedback pack: a ready-made prompt, plus the document, for the user to
/// take to a chat assistant themselves. The app never sends anything anywhere.
/// Only the role, company, location, job description (or, without one, the posting link minus its
/// query string), the document and its file name go in: never notes, contacts or other personal details.
/// </summary>
public partial class FeedbackPackService(IDbContextFactory<TrackerDbContext> dbFactory, DocumentService documents)
{
    /// <summary>The prompt's file name inside the zip.</summary>
    public const string PromptFileName = "prompt.md";

    private static readonly Lazy<string> Template = new(LoadTemplate);

    /// <returns>Null if the application doesn't exist or the document isn't linked to it.</returns>
    public async Task<FeedbackPack?> BuildAsync(int applicationId, int documentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Applications.AsNoTracking()
            .Where(a => a.Id == applicationId)
            .Select(a => new
            {
                a.RoleTitle,
                CompanyName = a.Company.Name,
                a.Location,
                a.PostingUrl,
                a.PostingText,
                Document = a.Documents
                    .Where(d => d.Id == documentId)
                    .Select(d => new { d.Id, d.Title, d.Kind, d.OriginalFileName })
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct);

        if (row?.Document is not { } document)
        {
            return null;
        }

        var kind = KindText(document.Kind);
        var hasPostingText = !string.IsNullOrWhiteSpace(row.PostingText);

        // The name the document has inside the zip, so the prompt and the pack agree.
        var fileName = SafeEntryName(document.OriginalFileName);
        var values = new Dictionary<string, string>
        {
            ["DOCUMENT_KIND"] = kind,
            ["DOCUMENT_FILE_NAME"] = OneLine(fileName),
            ["ROLE"] = OneLine(row.RoleTitle),
            ["COMPANY"] = OneLine(row.CompanyName),
            ["LOCATION"] = string.IsNullOrWhiteSpace(row.Location) ? "not stated" : OneLine(row.Location),
            ["POSTING_SECTION"] = PostingSection(row.PostingText, row.PostingUrl),
        };

        // One pass over the template, so text the user typed is never itself treated as a placeholder.
        var prompt = Placeholder().Replace(Template.Value, m => values[m.Groups[1].Value]);

        return new FeedbackPack(
            prompt,
            document.Id,
            document.Title,
            fileName,
            hasPostingText,
            PackFileName(kind, row.CompanyName, row.RoleTitle));
    }

    /// <summary>A zip holding <see cref="PromptFileName"/> and the document under its original name.</summary>
    /// <returns>Null if the pack can't be built, or the document's file is missing.</returns>
    public async Task<FeedbackPackFile?> CreateZipAsync(int applicationId, int documentId, CancellationToken ct = default)
    {
        var pack = await BuildAsync(applicationId, documentId, ct);
        if (pack is null)
        {
            return null;
        }

        var file = await documents.OpenFileAsync(documentId, ct);
        if (file is null)
        {
            return null;
        }

        var zip = new MemoryStream();
        await using (file.Content)
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var promptEntry = archive.CreateEntry(PromptFileName, CompressionLevel.Optimal);
            await using (var writer = new StreamWriter(promptEntry.Open(), new UTF8Encoding(false)))
            {
                await writer.WriteAsync(pack.Prompt);
            }

            var documentEntry = archive.CreateEntry(pack.DocumentFileName, CompressionLevel.Optimal);
            await using (var entry = documentEntry.Open())
            {
                await file.Content.CopyToAsync(entry, ct);
            }
        }

        zip.Position = 0;
        return new FeedbackPackFile(zip, pack.PackFileName);
    }

    private static string KindText(DocumentKind kind) => kind switch
    {
        DocumentKind.Cv => "CV",
        DocumentKind.CoverLetter => "cover letter",
        _ => "document",
    };

    private static string PostingSection(string? postingText, string? postingUrl)
    {
        if (!string.IsNullOrWhiteSpace(postingText))
        {
            // Tags mark exactly where the pasted text starts and ends. Any such tag inside the pasted
            // text is neutralised first, so the text can't close the block early and pose as the request.
            var body = JobDescriptionTag().Replace(Normalise(postingText).Trim(), "[job_description tag removed]");
            return "## Job description\n\n<job_description>\n" + body + "\n</job_description>";
        }

        var url = PostingLink(postingUrl);
        var link = url is null ? "" : $" The posting was at {url}, but please don't rely on fetching it.";
        return "## Job description\n\nI don't have the job description to hand." + link +
               " Base your feedback on the role title and company, and tell me which points would change if you had the full description.";
    }

    /// <summary>
    /// E.g. <c>cv-feedback-contoso-data-analyst.zip</c>: ASCII letters, digits and hyphens only, at most 80
    /// characters before <c>.zip</c>. It always starts with the kind (<c>cv-feedback</c>, …), so it is never empty.
    /// </summary>
    private static string PackFileName(string kind, string company, string role)
    {
        var slug = Slug($"{kind} feedback {company} {role}");
        if (slug.Length > 80)
        {
            slug = slug[..80].TrimEnd('-');
        }
        return slug + ".zip";
    }

    private static string Slug(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD).ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(c);
            }
            else if (char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark
                     && builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }
        return builder.ToString().Trim('-');
    }

    /// <summary>The document's name inside the zip: no folders, and something sensible if it's odd.</summary>
    private static string SafeEntryName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        // Case-insensitive: "Prompt.md" would overwrite prompt.md when unzipped on Windows.
        return string.IsNullOrWhiteSpace(name) || string.Equals(name, PromptFileName, StringComparison.OrdinalIgnoreCase)
            ? "document" + Path.GetExtension(fileName).ToLowerInvariant()
            : name;
    }

    /// <summary>
    /// Keeps a single value on one line so it can't break the prompt's structure: line breaks, tabs and
    /// other control characters become single spaces.
    /// </summary>
    private static string OneLine(string value)
    {
        var cleaned = new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Normalise(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>
    /// The posting link without its query string or fragment, which on job boards often carry tracking
    /// or referral ids tied to the user's account. Null if there is no usable http(s) link.
    /// </summary>
    private static string? PostingLink(string? postingUrl)
    {
        if (string.IsNullOrWhiteSpace(postingUrl)
            || !Uri.TryCreate(postingUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return OneLine(uri.GetLeftPart(UriPartial.Path));
    }

    [GeneratedRegex(@"<\s*/?\s*job_description\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex JobDescriptionTag();

    [GeneratedRegex(@"\{\{(DOCUMENT_KIND|DOCUMENT_FILE_NAME|ROLE|COMPANY|LOCATION|POSTING_SECTION)\}\}")]
    private static partial Regex Placeholder();

    private static string LoadTemplate()
    {
        using var stream = typeof(FeedbackPackService).Assembly
            .GetManifestResourceStream("ApplicationTracker.Core.Feedback.cv-feedback-prompt.md")
            ?? throw new InvalidOperationException("The CV feedback prompt template is missing from the build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Normalise(reader.ReadToEnd());
    }
}
