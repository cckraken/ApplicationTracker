using System.ComponentModel.DataAnnotations;
using System.Text;
using ApplicationTracker.Core.Data;
using ApplicationTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ApplicationTracker.Core.Documents;

/// <summary>
/// The document library: uploads, lists, renames and deletes CV and cover-letter versions, links them
/// to applications, and opens their files. The only code that touches the <see cref="IDocumentStore"/>.
/// </summary>
public class DocumentService(
    IDbContextFactory<TrackerDbContext> dbFactory,
    IDocumentStore store,
    TimeProvider time,
    ILogger<DocumentService>? logger = null)
{
    private readonly ILogger log = logger ?? NullLogger<DocumentService>.Instance;

    /// <summary>The file types accepted, by extension (lower case, with the dot).</summary>
    private static readonly Dictionary<string, FileType> FileTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = new("application/pdf", "%PDF-"u8.ToArray()),
        [".docx"] = new("application/vnd.openxmlformats-officedocument.wordprocessingml.document", [0x50, 0x4B, 0x03, 0x04]),
        [".txt"] = new("text/plain; charset=utf-8", null),
        [".md"] = new("text/markdown; charset=utf-8", null),
    };

    /// <summary>The extensions accepted, for the upload form's file picker.</summary>
    public static string AcceptedExtensions => string.Join(",", FileTypes.Keys);

    private const int MaxFileNameLength = 255;

    /// <summary>
    /// Stores the file and adds it to the library. The content is read in full (it is at most
    /// <see cref="Document.MaxSizeBytes"/>) so its type can be checked before anything is saved.
    /// </summary>
    /// <returns>The new document's id.</returns>
    /// <exception cref="ValidationException">The input or the file is not acceptable.</exception>
    public async Task<int> UploadAsync(DocumentInput input, Stream content, string fileName, CancellationToken ct = default)
    {
        var (kind, title) = Validate(input);
        var originalName = Path.GetFileName(fileName ?? "").Trim();
        if (originalName.Length == 0 || Path.GetFileNameWithoutExtension(originalName).Trim().Length == 0)
        {
            throw new ValidationException("Choose a file to upload.");
        }
        if (originalName.Length > MaxFileNameLength)
        {
            throw new ValidationException("The file name is too long. Rename the file and try again.");
        }

        var extension = Path.GetExtension(originalName);
        if (!FileTypes.TryGetValue(extension, out var type))
        {
            throw new ValidationException("Upload a PDF, Word (.docx), text (.txt) or Markdown (.md) file.");
        }

        var bytes = await ReadUpToLimitAsync(content, ct);
        if (bytes.Length == 0)
        {
            throw new ValidationException("That file is empty.");
        }
        if (!LooksLike(type, bytes))
        {
            throw new ValidationException(type.Signature is null
                ? "That text file isn't UTF-8. Save it as UTF-8 (in Notepad: File › Save as › Encoding: UTF-8) and try again."
                : $"That file isn't a valid {extension.TrimStart('.').ToUpperInvariant()} file.");
        }

        var storedName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        using (var stream = new MemoryStream(bytes, writable: false))
        {
            await store.SaveAsync(storedName, stream, ct);
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var document = new Document
            {
                Kind = kind,
                Title = title,
                OriginalFileName = originalName,
                StoredFileName = storedName,
                ContentType = type.ContentType,
                SizeBytes = bytes.Length,
                CreatedAt = time.GetUtcNow().UtcDateTime,
            };
            db.Documents.Add(document);
            await db.SaveChangesAsync(ct);
            return document.Id;
        }
        catch
        {
            // Don't leave an unreferenced copy of a personal file behind, but never let a failed
            // clean-up hide the original error.
            TryDeleteFile(storedName);
            throw;
        }
    }

    /// <summary>The library, newest first, optionally only one kind.</summary>
    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(DocumentKind? kind = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        IQueryable<Document> query = db.Documents.AsNoTracking();
        if (kind is DocumentKind k)
        {
            query = query.Where(d => d.Kind == k);
        }

        return await Project(query.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id)).ToListAsync(ct);
    }

    /// <summary>The documents linked to an application, newest first. Empty if it doesn't exist.</summary>
    public async Task<IReadOnlyList<DocumentSummary>> GetForApplicationAsync(int applicationId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.Documents.AsNoTracking()
            .Where(d => d.Applications.Any(a => a.Id == applicationId))
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id);
        return await Project(query).ToListAsync(ct);
    }

    /// <summary>
    /// The document's current kind and title as form input, or null if it doesn't exist. The values
    /// are also recorded as <see cref="DocumentInput.OriginalKind"/> / <see cref="DocumentInput.OriginalTitle"/>
    /// so <see cref="UpdateAsync"/> can tell if they changed elsewhere while the form was open.
    /// </summary>
    public async Task<DocumentInput?> GetForEditAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Documents.AsNoTracking()
            .Where(d => d.Id == id)
            .Select(d => new DocumentInput { Kind = d.Kind, Title = d.Title, OriginalKind = d.Kind, OriginalTitle = d.Title })
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>
    /// Changes the kind and title. The file is unchanged. When the input carries the values the form
    /// started from (<see cref="DocumentInput.OriginalTitle"/>), nothing is saved if the stored values
    /// no longer match them, so an old form can't overwrite a newer rename.
    /// </summary>
    /// <exception cref="ValidationException">The input is invalid.</exception>
    public async Task<DocumentUpdateResult> UpdateAsync(int id, DocumentInput input, CancellationToken ct = default)
    {
        var (kind, title) = Validate(input);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
        {
            return DocumentUpdateResult.NotFound;
        }

        if (input.OriginalTitle is not null
            && (document.Title != input.OriginalTitle || document.Kind != input.OriginalKind))
        {
            return DocumentUpdateResult.ChangedElsewhere;
        }

        document.Kind = kind;
        document.Title = title;
        await db.SaveChangesAsync(ct);
        return DocumentUpdateResult.Saved;
    }

    /// <summary>
    /// Removes the document, its links to applications, and its file. The row goes first, so a
    /// failure to delete the file (for example while it is being downloaded) leaves at worst an
    /// orphaned file, which is logged; the delete itself still counts as done.
    /// </summary>
    /// <returns>False if the document doesn't exist.</returns>
    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var document = await db.Documents.Include(d => d.Applications).SingleOrDefaultAsync(d => d.Id == id, ct);
        if (document is null)
        {
            return false;
        }

        document.Applications.Clear();
        db.Documents.Remove(document);
        await db.SaveChangesAsync(ct);
        TryDeleteFile(document.StoredFileName);
        return true;
    }

    /// <summary>Deletes a stored file, logging instead of throwing if it can't (locked, no access).</summary>
    private void TryDeleteFile(string storedFileName)
    {
        try
        {
            store.Delete(storedFileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Couldn't delete document file {StoredFileName}; it is left orphaned", storedFileName);
        }
    }

    /// <summary>Links the document to the application; already linked is fine.</summary>
    /// <returns>False if either doesn't exist.</returns>
    public async Task<bool> LinkAsync(int applicationId, int documentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var application = await db.Applications.Include(a => a.Documents).SingleOrDefaultAsync(a => a.Id == applicationId, ct);
        var document = await db.Documents.SingleOrDefaultAsync(d => d.Id == documentId, ct);
        if (application is null || document is null)
        {
            return false;
        }

        if (!application.Documents.Contains(document))
        {
            application.Documents.Add(document);
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    /// <summary>Removes the link; not linked is fine. The document stays in the library.</summary>
    /// <returns>False if the application doesn't exist.</returns>
    public async Task<bool> UnlinkAsync(int applicationId, int documentId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var application = await db.Applications.Include(a => a.Documents).SingleOrDefaultAsync(a => a.Id == applicationId, ct);
        if (application is null)
        {
            return false;
        }

        var linked = application.Documents.FirstOrDefault(d => d.Id == documentId);
        if (linked is not null)
        {
            application.Documents.Remove(linked);
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    /// <summary>Opens the document's file for download.</summary>
    /// <returns>Null if the document or its file doesn't exist.</returns>
    public async Task<DocumentFile?> OpenFileAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Documents.AsNoTracking()
            .Where(d => d.Id == id)
            .Select(d => new { d.StoredFileName, d.ContentType, d.OriginalFileName })
            .SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }

        var stream = store.OpenRead(row.StoredFileName);
        return stream is null ? null : new DocumentFile(stream, row.ContentType, row.OriginalFileName);
    }

    private static (DocumentKind Kind, string Title) Validate(DocumentInput input)
    {
        Validator.ValidateObject(input, new ValidationContext(input), validateAllProperties: true);
        var title = input.Title.Trim();
        if (title.Length == 0)
        {
            throw new ValidationException("Give this version a name, like \"CV – data roles v3\".");
        }
        if (!Enum.IsDefined(input.Kind!.Value))
        {
            throw new ValidationException("Choose what kind of document this is.");
        }
        return (input.Kind.Value, title);
    }

    /// <summary>Reads the whole stream, refusing anything over the size limit.</summary>
    private static async Task<byte[]> ReadUpToLimitAsync(Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > Document.MaxSizeBytes)
            {
                throw new ValidationException($"That file is too large. The limit is {Document.MaxSizeBytes / (1024 * 1024)} MB.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Checks the file's first bytes; text files must be UTF-8 without NUL bytes.</summary>
    private static bool LooksLike(FileType type, byte[] bytes)
    {
        if (type.Signature is byte[] signature)
        {
            return bytes.AsSpan().StartsWith(signature);
        }

        if (bytes.AsSpan().Contains((byte)0))
        {
            return false;
        }
        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static IQueryable<DocumentSummary> Project(IQueryable<Document> query) =>
        query.Select(d => new DocumentSummary(
            d.Id, d.Kind, d.Title, d.OriginalFileName, d.SizeBytes, d.CreatedAt, d.Applications.Count));

    private sealed record FileType(string ContentType, byte[]? Signature);
}
