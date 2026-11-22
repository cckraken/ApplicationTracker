using ApplicationTracker.Core.Data;
using ApplicationTracker.Core.Domain;
using ApplicationTracker.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace ApplicationTracker.Tests.Data;

public sealed class DemoDataSeederTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Seeds_an_empty_database_once()
    {
        using (var db = _db.CreateDbContext())
        {
            await DemoDataSeeder.SeedIfEmptyAsync(db, _time);
            await DemoDataSeeder.SeedIfEmptyAsync(db, _time);
        }

        using var check = _db.CreateDbContext();
        Assert.Equal(12, await check.Applications.CountAsync());
        Assert.Contains(await check.Applications.Select(a => a.CurrentStatus).ToListAsync(), s => s == ApplicationStatus.Offer);
        Assert.True(await check.StatusChanges.AnyAsync());
    }

    [Fact]
    public async Task Seeds_notes_and_contacts_for_later_stage_applications()
    {
        using (var db = _db.CreateDbContext())
        {
            await DemoDataSeeder.SeedIfEmptyAsync(db, _time);
        }

        using var check = _db.CreateDbContext();
        // Screening (2) + Interview (2) + Offer (1) each get a recruiter; Interview and Offer also a
        // hiring manager; plus one unlinked contact.
        Assert.Equal(5 + 3 + 1, await check.Contacts.CountAsync());
        Assert.Equal(1, await check.Contacts.CountAsync(c => !c.Applications.Any()));
        Assert.True(await check.Notes.AnyAsync(n => n.UpdatedAt != null));
        Assert.True(await check.Applications
            .Where(a => a.CurrentStatus == ApplicationStatus.Offer)
            .AllAsync(a => a.Contacts.Count == 2 && a.Notes.Count == 2));
    }

    [Fact]
    public async Task Does_not_touch_a_database_that_has_data()
    {
        using (var db = _db.CreateDbContext())
        {
            db.Applications.Add(new JobApplication { RoleTitle = "Real", Company = new Company { Name = "Real Co" } });
            await db.SaveChangesAsync();
            await DemoDataSeeder.SeedIfEmptyAsync(db, _time);
        }

        using var check = _db.CreateDbContext();
        Assert.Equal(1, await check.Applications.CountAsync());
    }

    [Fact]
    public async Task SeedIfEmptyAsync_SeededContactsAndNotes_ReuseApplicationCompaniesAndHaveConsistentTimestamps()
    {
        using (var db = _db.CreateDbContext())
        {
            await DemoDataSeeder.SeedIfEmptyAsync(db, _time);
        }

        using var check = _db.CreateDbContext();
        // Contacts share their application's company rather than creating duplicates.
        Assert.Equal(12, await check.Companies.CountAsync());
        Assert.True(await check.Contacts
            .Where(c => c.Applications.Any())
            .AllAsync(c => c.Applications.All(a => a.CompanyId == c.CompanyId)));

        // An edited note was edited after it was written, and nothing is dated in the future.
        var utcNow = _time.GetUtcNow().UtcDateTime;
        var notes = await check.Notes.ToListAsync();
        Assert.All(notes.Where(n => n.UpdatedAt is not null), n => Assert.True(n.UpdatedAt > n.CreatedAt));
        Assert.All(notes, n => Assert.True(n.CreatedAt <= utcNow && (n.UpdatedAt ?? n.CreatedAt) <= utcNow));
    }
}
