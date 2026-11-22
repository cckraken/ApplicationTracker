using System.ComponentModel.DataAnnotations;
using ApplicationTracker.Core.Data;
using ApplicationTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace ApplicationTracker.Core.Notes;

/// <summary>One note as shown on the detail page. Timestamps are UTC.</summary>
public sealed record NoteView(int Id, string Body, DateTime CreatedAt, DateTime? UpdatedAt);

/// <summary>Adds, edits and deletes notes on an application.</summary>
public class NoteService(IDbContextFactory<TrackerDbContext> dbFactory, TimeProvider time)
{
    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    /// <returns>The new note, or null if the application doesn't exist.</returns>
    /// <exception cref="ValidationException">The body is empty or too long.</exception>
    public async Task<NoteView?> AddAsync(int applicationId, string body, CancellationToken ct = default)
    {
        var text = ValidBody(body);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Applications.AnyAsync(a => a.Id == applicationId, ct))
        {
            return null;
        }

        var note = new Note { JobApplicationId = applicationId, Body = text, CreatedAt = UtcNow };
        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);
        return View(note);
    }

    /// <summary>Replaces the body. Saving the same text again changes nothing (no "edited" mark).</summary>
    /// <returns>The note as saved, or null if it doesn't exist.</returns>
    /// <exception cref="ValidationException">The body is empty or too long.</exception>
    public async Task<NoteView?> UpdateAsync(int noteId, string body, CancellationToken ct = default)
    {
        var text = ValidBody(body);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var note = await db.Notes.SingleOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null)
        {
            return null;
        }

        if (!string.Equals(note.Body, text, StringComparison.Ordinal))
        {
            note.Body = text;
            note.UpdatedAt = UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return View(note);
    }

    /// <returns>False if the note doesn't exist.</returns>
    public async Task<bool> DeleteAsync(int noteId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Notes.Where(n => n.Id == noteId).ExecuteDeleteAsync(ct) > 0;
    }

    /// <summary>Trims surrounding whitespace (line breaks inside are kept) and checks the length.</summary>
    private static string ValidBody(string? body)
    {
        var text = body?.Trim() ?? "";
        if (text.Length == 0)
        {
            throw new ValidationException("Write something in the note first.");
        }
        if (text.Length > Note.MaxBodyLength)
        {
            throw new ValidationException($"A note can be at most {Note.MaxBodyLength:N0} characters.");
        }
        return text;
    }

    private static NoteView View(Note note) => new(note.Id, note.Body, note.CreatedAt, note.UpdatedAt);
}
