using System.ComponentModel.DataAnnotations;
using ApplicationTracker.Core.Applications;
using ApplicationTracker.Core.Data;
using ApplicationTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace ApplicationTracker.Core.Contacts;

/// <summary>Recruiters, hiring managers and interviewers, and their links to applications.</summary>
public class ContactService(IDbContextFactory<TrackerDbContext> dbFactory)
{
    /// <summary>
    /// Contacts whose name, role, email or company contains <paramref name="search"/>
    /// (Unicode-aware, ignoring case, literal), by name, with the applications each is linked to.
    /// </summary>
    public async Task<IReadOnlyList<ContactListItem>> SearchAsync(string? search, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        IQueryable<Contact> query = db.Contacts.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(c =>
                TrackerDbContext.UnicodeLower(c.Name).Contains(term) ||
                (c.Role != null && TrackerDbContext.UnicodeLower(c.Role).Contains(term)) ||
                (c.Email != null && TrackerDbContext.UnicodeLower(c.Email).Contains(term)) ||
                (c.Company != null && TrackerDbContext.UnicodeLower(c.Company.Name).Contains(term)));
        }

        return await query
            .OrderBy(c => TrackerDbContext.UnicodeLower(c.Name))
            .ThenBy(c => c.Id)
            .Select(c => new ContactListItem(
                new ContactSummary(c.Id, c.Name, c.Role, c.Email, c.Phone, c.LinkedInUrl,
                    c.Company == null ? null : c.Company.Name),
                c.Applications
                    .OrderBy(a => a.Id)
                    .Select(a => new LinkedApplication(a.Id, a.RoleTitle, a.Company.Name, a.CurrentStatus))
                    .ToList()))
            .AsSplitQuery()
            .ToListAsync(ct);
    }

    /// <summary>The contact's current values as form input, or null if it doesn't exist.</summary>
    public async Task<ContactInput?> GetForEditAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Contacts.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new ContactInput
            {
                Name = c.Name,
                Role = c.Role,
                Email = c.Email,
                Phone = c.Phone,
                LinkedInUrl = c.LinkedInUrl,
                Company = c.Company == null ? null : c.Company.Name,
            })
            .SingleOrDefaultAsync(ct);
    }

    /// <returns>The new contact's id.</returns>
    /// <exception cref="ValidationException">The input is invalid.</exception>
    public async Task<int> CreateAsync(ContactInput input, CancellationToken ct = default)
    {
        Validate(input);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var contact = new Contact { Name = input.Name.Trim() };
        await ApplyAsync(db, contact, input, ct);
        db.Contacts.Add(contact);
        await db.SaveChangesAsync(ct);
        return contact.Id;
    }

    /// <summary>Creates a contact and links it to the application in one save.</summary>
    /// <returns>The new contact's id, or null (nothing created) if the application doesn't exist.</returns>
    /// <exception cref="ValidationException">The input is invalid.</exception>
    public async Task<int?> CreateForApplicationAsync(int applicationId, ContactInput input, CancellationToken ct = default)
    {
        Validate(input);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = await db.Applications.SingleOrDefaultAsync(a => a.Id == applicationId, ct);
        if (app is null)
        {
            return null;
        }

        var contact = new Contact { Name = input.Name.Trim() };
        await ApplyAsync(db, contact, input, ct);
        contact.Applications.Add(app);
        db.Contacts.Add(contact);
        await db.SaveChangesAsync(ct);
        return contact.Id;
    }

    /// <returns>False if the contact doesn't exist.</returns>
    /// <exception cref="ValidationException">The input is invalid.</exception>
    public async Task<bool> UpdateAsync(int id, ContactInput input, CancellationToken ct = default)
    {
        Validate(input);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var contact = await db.Contacts.Include(c => c.Company).SingleOrDefaultAsync(c => c.Id == id, ct);
        if (contact is null)
        {
            return false;
        }

        var previousCompany = contact.Company;
        contact.Name = input.Name.Trim();

        // Fixing only the capitalisation of a company nothing else uses: rename it (as the
        // application form does). The lookup below matches ignoring case, so it would
        // otherwise keep the old spelling.
        var companyName = Clean(input.Company);
        if (previousCompany is not null && companyName is not null
            && !string.Equals(previousCompany.Name, companyName, StringComparison.Ordinal)
            && previousCompany.Name.ToLowerInvariant() == companyName.ToLowerInvariant()
            && !await CompanyLookup.IsUsedElsewhereAsync(db, previousCompany, exceptContactId: contact.Id, ct))
        {
            previousCompany.Name = companyName;
        }

        await ApplyAsync(db, contact, input, ct);

        if (previousCompany is not null && !ReferenceEquals(previousCompany, contact.Company))
        {
            await CompanyLookup.RemoveIfUnusedAsync(db, previousCompany, exceptContactId: contact.Id, ct);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Permanently deletes the contact and its links. Linked applications are kept.</summary>
    /// <returns>False if the contact doesn't exist.</returns>
    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var contact = await db.Contacts.Include(c => c.Company).SingleOrDefaultAsync(c => c.Id == id, ct);
        if (contact is null)
        {
            return false;
        }

        db.Contacts.Remove(contact); // The ApplicationContacts links cascade.
        if (contact.Company is { } company)
        {
            await CompanyLookup.RemoveIfUnusedAsync(db, company, exceptContactId: contact.Id, ct);
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Links an existing contact to an application. Linking twice is harmless.</summary>
    /// <returns>False if either doesn't exist.</returns>
    public async Task<bool> LinkAsync(int applicationId, int contactId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var contact = await db.Contacts
            .Include(c => c.Applications.Where(a => a.Id == applicationId))
            .SingleOrDefaultAsync(c => c.Id == contactId, ct);
        if (contact is null)
        {
            return false;
        }
        if (contact.Applications.Count > 0)
        {
            return true;
        }

        var app = await db.Applications.SingleOrDefaultAsync(a => a.Id == applicationId, ct);
        if (app is null)
        {
            return false;
        }

        contact.Applications.Add(app);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Removes the link; the contact itself is kept.</summary>
    /// <returns>False if there was no such link.</returns>
    public async Task<bool> UnlinkAsync(int applicationId, int contactId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var contact = await db.Contacts
            .Include(c => c.Applications.Where(a => a.Id == applicationId))
            .SingleOrDefaultAsync(c => c.Id == contactId, ct);
        if (contact is null || contact.Applications.Count == 0)
        {
            return false;
        }

        contact.Applications.Clear();
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Contacts that could be linked to the application: not linked yet, those at the
    /// application's company first, then by name.
    /// </summary>
    public async Task<IReadOnlyList<ContactSummary>> GetLinkCandidatesAsync(int applicationId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var companyId = await db.Applications
            .Where(a => a.Id == applicationId)
            .Select(a => (int?)a.CompanyId)
            .SingleOrDefaultAsync(ct);
        if (companyId is null)
        {
            return [];
        }

        return await db.Contacts.AsNoTracking()
            .Where(c => !c.Applications.Any(a => a.Id == applicationId))
            .OrderBy(c => c.CompanyId == companyId ? 0 : 1)
            .ThenBy(c => TrackerDbContext.UnicodeLower(c.Name))
            .ThenBy(c => c.Id)
            .Select(c => new ContactSummary(c.Id, c.Name, c.Role, c.Email, c.Phone, c.LinkedInUrl,
                c.Company == null ? null : c.Company.Name))
            .ToListAsync(ct);
    }

    private static async Task ApplyAsync(TrackerDbContext db, Contact contact, ContactInput input, CancellationToken ct)
    {
        contact.Role = Clean(input.Role);
        contact.Email = Clean(input.Email);
        contact.Phone = Clean(input.Phone);
        contact.LinkedInUrl = Clean(input.LinkedInUrl);

        var companyName = Clean(input.Company);
        contact.Company = companyName is null ? null : await CompanyLookup.FindOrCreateAsync(db, companyName, ct);
        if (contact.Company is null)
        {
            contact.CompanyId = null;
        }
    }

    /// <summary>Validates the trimmed values, so a field of only spaces counts as empty, not as an invalid email.</summary>
    private static void Validate(ContactInput input)
    {
        var cleaned = new ContactInput
        {
            Name = input.Name?.Trim() ?? "",
            Role = Clean(input.Role),
            Email = Clean(input.Email),
            Phone = Clean(input.Phone),
            LinkedInUrl = Clean(input.LinkedInUrl),
            Company = Clean(input.Company),
        };
        Validator.ValidateObject(cleaned, new ValidationContext(cleaned), validateAllProperties: true);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
