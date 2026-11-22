using ApplicationTracker.Core.Data;
using ApplicationTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace ApplicationTracker.Core.Applications;

/// <summary>Company matching shared by applications and contacts.</summary>
internal static class CompanyLookup
{
    /// <summary>
    /// The existing company with this name (ignoring case, Unicode-aware), or a new unsaved one.
    /// </summary>
    public static async Task<Company> FindOrCreateAsync(TrackerDbContext db, string name, CancellationToken ct)
    {
        var trimmed = name.Trim();
        var lowered = trimmed.ToLowerInvariant();
        return await db.Companies.FirstOrDefaultAsync(c => TrackerDbContext.UnicodeLower(c.Name) == lowered, ct)
            ?? new Company { Name = trimmed };
    }

    /// <summary>
    /// Removes <paramref name="company"/> if nothing but <paramref name="exceptContactId"/> uses it,
    /// so a typo'd or abandoned company doesn't stay in the suggestions.
    /// </summary>
    public static async Task RemoveIfUnusedAsync(
        TrackerDbContext db, Company company, int? exceptContactId, CancellationToken ct)
    {
        if (!await IsUsedElsewhereAsync(db, company, exceptContactId, ct))
        {
            db.Companies.Remove(company);
        }
    }

    /// <summary>
    /// True if any application, or any contact other than <paramref name="exceptContactId"/>,
    /// uses the company (as stored in the database).
    /// </summary>
    public static async Task<bool> IsUsedElsewhereAsync(
        TrackerDbContext db, Company company, int? exceptContactId, CancellationToken ct) =>
        await db.Applications.AnyAsync(a => a.CompanyId == company.Id, ct) ||
        await db.Contacts.AnyAsync(c => c.CompanyId == company.Id && c.Id != exceptContactId, ct);
}
