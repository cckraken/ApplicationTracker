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

    [Fact]
    public async Task Seeds_one_reminder_in_each_state()
    {
        using (var db = _db.CreateDbContext())
        {
            await DemoDataSeeder.SeedIfEmptyAsync(db, _time);
        }

        using var check = _db.CreateDbContext();
        var today = new DateOnly(2026, 11, 21);
        var reminders = await check.Reminders.Include(r => r.JobApplication!).ThenInclude(a => a.Company).ToListAsync();
        Assert.Equal(4, reminders.Count);

        var overdue = Assert.Single(reminders, r => !r.IsDone && r.DueOn < today);
        Assert.Null(overdue.JobApplicationId);

        var dueToday = Assert.Single(reminders, r => !r.IsDone && r.DueOn == today);
        Assert.Equal("Adventure Works", dueToday.JobApplication?.Company.Name);

        var upcoming = Assert.Single(reminders, r => !r.IsDone && r.DueOn > today);
        Assert.Equal("Proseware", upcoming.JobApplication?.Company.Name);

        var done = Assert.Single(reminders, r => r.IsDone);
        Assert.Null(done.JobApplicationId);
        Assert.NotNull(done.CompletedAt);
    }

    [Fact]
    public async Task Seeded_reminders_have_consistent_utc_timestamps()
    {
        using (var db = _db.CreateDbContext())
        {
            await DemoDataSeeder.SeedIfEmptyAsync(db, _time);
        }

        using var check = _db.CreateDbContext();
        var utcNow = _time.GetUtcNow().UtcDateTime;
        var reminders = await check.Reminders.ToListAsync();
        Assert.All(reminders, r =>
        {
            Assert.Equal(DateTimeKind.Utc, r.CreatedAt.Kind);
            Assert.True(r.CreatedAt <= utcNow);
            Assert.Equal(r.IsDone, r.CompletedAt is not null);
            if (r.CompletedAt is DateTime completed)
            {
                Assert.True(completed > r.CreatedAt && completed <= utcNow);
            }
        });
    }

    [Fact]
    public async Task Seeded_reminder_dates_follow_the_local_date_late_in_the_evening()
    {
        // 23:30 UTC on the 21st is already the 22nd in UTC+1.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 23, 30, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("Test+01", TimeSpan.FromHours(1), "Test +1", "Test +1"));
        using (var db = _db.CreateDbContext())
        {
            await DemoDataSeeder.SeedIfEmptyAsync(db, time);
        }

        using var check = _db.CreateDbContext();
        var dueToday = await check.Reminders.SingleAsync(r => r.JobApplication!.Company.Name == "Adventure Works");
        Assert.Equal(new DateOnly(2026, 11, 22), dueToday.DueOn);
    }
}
