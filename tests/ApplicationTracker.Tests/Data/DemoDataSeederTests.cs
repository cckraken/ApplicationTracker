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
}
