using ApplicationTracker.Core.Applications;
using ApplicationTracker.Core.Contacts;
using ApplicationTracker.Core.Domain;
using ApplicationTracker.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace ApplicationTracker.Tests.Applications;

/// <summary>Regression tests for issues found in code review of the list and form.</summary>
public sealed class ApplicationServiceUpdateTests : IDisposable
{
    private readonly SqliteTestDatabase _db = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly ApplicationService _service;

    public ApplicationServiceUpdateTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _service = new ApplicationService(_db, _time);
    }

    public void Dispose() => _db.Dispose();

    private Task<int> Create(string company, string role = "Analyst", ApplicationStatus status = ApplicationStatus.Wishlist) =>
        _service.CreateAsync(new ApplicationInput { Company = company, RoleTitle = role, Status = status });

    [Fact]
    public async Task Saving_a_stale_form_does_not_undo_a_status_change_made_meanwhile()
    {
        var id = await Create("Contoso", status: ApplicationStatus.Screening);
        var staleForm = await _service.GetForEditAsync(id);

        // Meanwhile the status is changed from the list.
        await _service.ChangeStatusAsync(id, ApplicationStatus.Interview);

        // The user saves the form they loaded earlier, only editing the follow-up date.
        staleForm!.FollowUpOn = new DateOnly(2026, 12, 1);
        await _service.UpdateAsync(id, staleForm);

        using var db = _db.CreateDbContext();
        var app = await db.Applications.Include(a => a.StatusHistory).SingleAsync();
        Assert.Equal(ApplicationStatus.Interview, app.CurrentStatus);
        Assert.Equal(new DateOnly(2026, 12, 1), app.FollowUpOn);
        Assert.DoesNotContain(app.StatusHistory, h => h.ToStatus == ApplicationStatus.Screening && h.FromStatus == ApplicationStatus.Interview);
    }

    [Fact]
    public async Task Changing_status_in_the_form_still_applies()
    {
        var id = await Create("Contoso", status: ApplicationStatus.Applied);
        var form = await _service.GetForEditAsync(id);

        form!.Status = ApplicationStatus.Interview;
        await _service.UpdateAsync(id, form);

        Assert.Equal(ApplicationStatus.Interview, (await _service.GetForEditAsync(id))!.Status);
    }

    [Fact]
    public async Task Fixing_company_capitalisation_renames_it_when_only_this_application_uses_it()
    {
        var id = await Create("contoso");
        var form = await _service.GetForEditAsync(id);

        form!.Company = "Contoso";
        await _service.UpdateAsync(id, form);

        Assert.Equal(["Contoso"], await _service.GetCompanyNamesAsync());
    }

    [Fact]
    public async Task Fixing_company_capitalisation_keeps_shared_company_name()
    {
        var id = await Create("contoso");
        await Create("contoso", role: "Other");
        var form = await _service.GetForEditAsync(id);

        form!.Company = "CONTOSO";
        await _service.UpdateAsync(id, form);

        Assert.Equal(["contoso"], await _service.GetCompanyNamesAsync());
    }

    [Fact]
    public async Task Renaming_away_from_a_company_removes_it_when_nothing_else_uses_it()
    {
        var id = await Create("Gogle");
        var form = await _service.GetForEditAsync(id);

        form!.Company = "Google";
        await _service.UpdateAsync(id, form);

        Assert.Equal(["Google"], await _service.GetCompanyNamesAsync());
    }

    [Fact]
    public async Task Renaming_away_keeps_a_company_that_has_contacts()
    {
        var id = await Create("Contoso");
        using (var db = _db.CreateDbContext())
        {
            var company = await db.Companies.SingleAsync();
            db.Contacts.Add(new Contact { Name = "Sam Recruiter", CompanyId = company.Id });
            await db.SaveChangesAsync();
        }
        var form = await _service.GetForEditAsync(id);

        form!.Company = "Fabrikam";
        await _service.UpdateAsync(id, form);

        Assert.Equal(["Contoso", "Fabrikam"], await _service.GetCompanyNamesAsync());
    }

    [Fact]
    public async Task Company_sorting_and_suggestions_ignore_case()
    {
        await Create("zeta");
        await Create("Acme");
        await Create("beta");

        var sorted = await _service.ListAsync(new ApplicationFilter { SortBy = ApplicationSort.Company });

        Assert.Equal(["Acme", "beta", "zeta"], sorted.Select(a => a.CompanyName));
        Assert.Equal(["Acme", "beta", "zeta"], await _service.GetCompanyNamesAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Applied_sort_puts_not_yet_applied_last(bool descending)
    {
        await Create("Wishlist Co");
        await Create("Applied Co", status: ApplicationStatus.Applied);

        var sorted = await _service.ListAsync(new ApplicationFilter { SortBy = ApplicationSort.AppliedOn, Descending = descending });

        Assert.Equal("Wishlist Co", sorted[^1].CompanyName);
    }

    [Fact]
    public async Task UpdateAsync_CapitalisationFixOnCompanySharedWithAContact_KeepsTheSharedName()
    {
        var id = await Create("contoso");
        await new ContactService(_db).CreateAsync(new ContactInput { Name = "Sam Recruiter", Company = "contoso" });
        var form = await _service.GetForEditAsync(id);

        form!.Company = "Contoso";
        await _service.UpdateAsync(id, form);

        Assert.Equal(["contoso"], await _service.GetCompanyNamesAsync());
        Assert.Equal("contoso", (await _service.GetDetailsAsync(id))!.CompanyName);
    }

    [Fact]
    public async Task UpdateAsync_MovingToACompanyOnlyAContactUses_ReusesItAndRemovesTheOldOne()
    {
        var id = await Create("Gogle");
        await new ContactService(_db).CreateAsync(new ContactInput { Name = "Sam Recruiter", Company = "Google" });
        var form = await _service.GetForEditAsync(id);

        form!.Company = "GOOGLE";
        await _service.UpdateAsync(id, form);

        Assert.Equal(["Google"], await _service.GetCompanyNamesAsync());
        Assert.Equal("Google", (await _service.GetDetailsAsync(id))!.CompanyName);
    }
}
