namespace ApplicationTracker.Core.Documents;

/// <summary>Where document files are kept. Names are store-generated (see <see cref="DocumentService"/>).</summary>
public interface IDocumentStore
{
    /// <summary>
    /// Writes a new file. Fails if the name is already taken (names are unique GUIDs, so that means a bug).
    /// If the write fails partway, the partial file is removed before the exception is rethrown.
    /// </summary>
    Task SaveAsync(string storedFileName, Stream content, CancellationToken ct = default);

    /// <returns>The file's contents, or null if it doesn't exist.</returns>
    Stream? OpenRead(string storedFileName);

    /// <summary>Deletes the file; a missing file is fine.</summary>
    void Delete(string storedFileName);
}

/// <summary>Keeps document files in one folder (one per database, e.g. <c>data/tracker.dev-documents</c>, never committed).</summary>
public sealed class FileSystemDocumentStore(string directory) : IDocumentStore
{
    public string Directory { get; } = Path.GetFullPath(directory);

    public async Task SaveAsync(string storedFileName, Stream content, CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(storedFileName);
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        try
        {
            await using (file)
            {
                await content.CopyToAsync(file, ct);
            }
        }
        catch
        {
            File.Delete(path); // don't leave half a personal file behind
            throw;
        }
    }

    public Stream? OpenRead(string storedFileName)
    {
        var path = PathFor(storedFileName);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete) : null;
    }

    public void Delete(string storedFileName)
    {
        var path = PathFor(storedFileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>The full path for a stored name, refusing anything that could leave the folder.</summary>
    private string PathFor(string storedFileName)
    {
        if (string.IsNullOrWhiteSpace(storedFileName)
            || storedFileName != Path.GetFileName(storedFileName)
            || storedFileName.Contains(".."))
        {
            throw new ArgumentException("Not a valid stored file name.", nameof(storedFileName));
        }

        return Path.Combine(Directory, storedFileName);
    }
}
