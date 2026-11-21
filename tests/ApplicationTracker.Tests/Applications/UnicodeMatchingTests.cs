using ApplicationTracker.Core.Applications;
using ApplicationTracker.Core.Data;
using ApplicationTracker.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace ApplicationTracker.Tests.Applications;

/// <summary>SQLite's own lower()/LIKE only fold A-Z; these cover accented capitals.</summary>
public sealed class UnicodeMatchingTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();
    private readonly ApplicationService _service;

    public UnicodeMatchingTests() =>
        _service = new ApplicationService(_db, new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero)));

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Company_stored_in_accented_capitals_is_reused_from_lower_case_input()
    {
        await _service.CreateAsync(new ApplicationInput { Company = "MÜLLER GMBH", RoleTitle = "Analyst" });
        await _service.CreateAsync(new ApplicationInput { Company = "müller gmbh", RoleTitle = "Engineer" });

        using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.Companies.CountAsync());
    }

    [Fact]
    public async Task Search_in_lower_case_finds_accented_capitals()
    {
        await _service.CreateAsync(new ApplicationInput { Company = "ÉCOLE ÉLÉMENTAIRE", RoleTitle = "Teacher" });

        var result = await _service.ListAsync(new ApplicationFilter { Search = "école" });

        Assert.Single(result);
    }

    [Fact]
    public void UnicodeLower_also_works_outside_queries()
    {
        Assert.Equal("müller", TrackerDbContext.UnicodeLower("MÜLLER"));
    }
}
