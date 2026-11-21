using System.ComponentModel.DataAnnotations;
using ApplicationTracker.Core.Applications;
using ApplicationTracker.Core.Domain;
using ApplicationTracker.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace ApplicationTracker.Tests.Applications;

public sealed class ApplicationServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 21);

    private readonly SqliteTestDatabase _db = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly ApplicationService _service;

    public ApplicationServiceTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _service = new ApplicationService(_db, _time);
    }

    public void Dispose() => _db.Dispose();

    private static ApplicationInput Input(
        string company = "Contoso", string role = "Data Analyst",
        ApplicationStatus status = ApplicationStatus.Wishlist, DateOnly? followUpOn = null) =>
        new() { Company = company, RoleTitle = role, Status = status, FollowUpOn = followUpOn };

    [Fact]
    public async Task Create_trims_input_and_returns_new_id()
    {
        var id = await _service.CreateAsync(new ApplicationInput
        {
            Company = "  Contoso ",
            RoleTitle = " Data Analyst ",
            Location = "   ",
            Source = " LinkedIn ",
        });

        var saved = await _service.GetForEditAsync(id);
        Assert.NotNull(saved);
        Assert.Equal("Contoso", saved.Company);
        Assert.Equal("Data Analyst", saved.RoleTitle);
        Assert.Null(saved.Location);
        Assert.Equal("LinkedIn", saved.Source);
    }

    [Fact]
    public async Task Create_with_non_wishlist_status_records_history_and_applied_date()
    {
        var id = await _service.CreateAsync(Input(status: ApplicationStatus.Applied));

        using var db = _db.CreateDbContext();
        var app = await db.Applications.Include(a => a.StatusHistory).SingleAsync(a => a.Id == id);
        Assert.Equal(ApplicationStatus.Applied, app.CurrentStatus);
        Assert.Single(app.StatusHistory);
        Assert.Equal(Today, app.AppliedOn);
    }

    [Fact]
    public async Task Create_reuses_existing_company_ignoring_case()
    {
        await _service.CreateAsync(Input(company: "Contoso"));
        await _service.CreateAsync(Input(company: "contoso", role: "Analyst II"));

        using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.Companies.CountAsync());
        Assert.Equal(["Contoso"], await _service.GetCompanyNamesAsync());
    }

    [Theory]
    [InlineData("", "Role")]
    [InlineData("Company", "")]
    public async Task Create_rejects_missing_required_fields(string company, string role)
    {
        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateAsync(Input(company, role)));
    }

    [Fact]
    public async Task Create_rejects_invalid_posting_url()
    {
        var input = Input();
        input.PostingUrl = "not a url";

        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateAsync(input));
    }

    [Fact]
    public async Task Update_changes_fields_company_and_status()
    {
        var id = await _service.CreateAsync(Input());
        _time.Advance(TimeSpan.FromDays(1));

        var updated = await _service.UpdateAsync(id, Input(company: "Fabrikam", role: "Senior Analyst", status: ApplicationStatus.Interview));

        Assert.True(updated);
        using var db = _db.CreateDbContext();
        var app = await db.Applications.Include(a => a.Company).Include(a => a.StatusHistory).SingleAsync();
        Assert.Equal("Fabrikam", app.Company.Name);
        Assert.Equal("Senior Analyst", app.RoleTitle);
        Assert.Equal(ApplicationStatus.Interview, app.CurrentStatus);
        Assert.Single(app.StatusHistory);
        Assert.Equal(Start.AddDays(1).UtcDateTime, app.UpdatedAt);
    }

    [Fact]
    public async Task Update_missing_application_returns_false()
    {
        Assert.False(await _service.UpdateAsync(999, Input()));
    }

    [Fact]
    public async Task ChangeStatus_records_history_and_ignores_same_status()
    {
        var id = await _service.CreateAsync(Input());

        Assert.True(await _service.ChangeStatusAsync(id, ApplicationStatus.Applied));
        Assert.True(await _service.ChangeStatusAsync(id, ApplicationStatus.Applied));
        Assert.False(await _service.ChangeStatusAsync(999, ApplicationStatus.Applied));

        using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.StatusChanges.CountAsync());
    }

    [Fact]
    public async Task Delete_removes_application_and_reports_missing()
    {
        var id = await _service.CreateAsync(Input());

        Assert.True(await _service.DeleteAsync(id));
        Assert.False(await _service.DeleteAsync(id));
        Assert.Null(await _service.GetForEditAsync(id));
    }

    [Fact]
    public async Task List_hides_closed_applications_unless_asked()
    {
        await _service.CreateAsync(Input(role: "Open"));
        await _service.CreateAsync(Input(role: "Rejected", status: ApplicationStatus.Rejected));
        await _service.CreateAsync(Input(role: "Withdrawn", status: ApplicationStatus.Withdrawn));

        var active = await _service.ListAsync(new ApplicationFilter());
        var all = await _service.ListAsync(new ApplicationFilter { IncludeClosed = true });
        var rejectedOnly = await _service.ListAsync(new ApplicationFilter { Status = ApplicationStatus.Rejected });

        Assert.Equal(["Open"], active.Select(a => a.RoleTitle));
        Assert.Equal(3, all.Count);
        Assert.Equal(["Rejected"], rejectedOnly.Select(a => a.RoleTitle));
    }

    [Theory]
    [InlineData("contoso", 1)]   // company, any case
    [InlineData("ANALYST", 2)]   // role, any case
    [InlineData("100%", 1)]      // LIKE wildcard is matched literally
    [InlineData("zzz", 0)]
    public async Task List_search_matches_company_or_role(string search, int expected)
    {
        await _service.CreateAsync(Input(company: "Contoso", role: "Data Analyst"));
        await _service.CreateAsync(Input(company: "Fabrikam", role: "Business Analyst"));
        await _service.CreateAsync(Input(company: "Litware", role: "100% remote engineer"));

        var result = await _service.ListAsync(new ApplicationFilter { Search = search });

        Assert.Equal(expected, result.Count);
    }

    [Fact]
    public async Task List_default_sort_is_follow_up_soonest_first_with_undated_last()
    {
        await _service.CreateAsync(Input(role: "None"));
        await _service.CreateAsync(Input(role: "Later", followUpOn: Today.AddDays(5)));
        await _service.CreateAsync(Input(role: "Overdue", followUpOn: Today.AddDays(-2)));

        var result = await _service.ListAsync(new ApplicationFilter());

        Assert.Equal(["Overdue", "Later", "None"], result.Select(a => a.RoleTitle));
    }

    [Fact]
    public async Task List_sorts_status_in_pipeline_order_not_alphabetically()
    {
        await _service.CreateAsync(Input(role: "Offer", status: ApplicationStatus.Offer));
        await _service.CreateAsync(Input(role: "Applied", status: ApplicationStatus.Applied));
        await _service.CreateAsync(Input(role: "Interview", status: ApplicationStatus.Interview));

        var result = await _service.ListAsync(new ApplicationFilter { SortBy = ApplicationSort.Status });

        Assert.Equal(["Applied", "Interview", "Offer"], result.Select(a => a.RoleTitle));
    }

    [Fact]
    public async Task List_sorts_by_company_descending()
    {
        await _service.CreateAsync(Input(company: "Alpha"));
        await _service.CreateAsync(Input(company: "Charlie"));
        await _service.CreateAsync(Input(company: "Bravo"));

        var result = await _service.ListAsync(new ApplicationFilter { SortBy = ApplicationSort.Company, Descending = true });

        Assert.Equal(["Charlie", "Bravo", "Alpha"], result.Select(a => a.CompanyName));
    }

    [Fact]
    public async Task List_reports_follow_up_state_relative_to_today()
    {
        await _service.CreateAsync(Input(role: "Overdue", followUpOn: Today.AddDays(-1)));
        await _service.CreateAsync(Input(role: "Today", followUpOn: Today));
        await _service.CreateAsync(Input(role: "Upcoming", followUpOn: Today.AddDays(1)));

        var states = (await _service.ListAsync(new ApplicationFilter())).ToDictionary(a => a.RoleTitle, a => a.FollowUpState);

        Assert.Equal(FollowUpState.Overdue, states["Overdue"]);
        Assert.Equal(FollowUpState.DueToday, states["Today"]);
        Assert.Equal(FollowUpState.Upcoming, states["Upcoming"]);
    }

    [Fact]
    public void Closed_applications_never_need_follow_up()
    {
        Assert.Equal(FollowUpState.None,
            ApplicationService.GetFollowUpState(ApplicationStatus.Rejected, Today.AddDays(-3), Today));
        Assert.Equal(FollowUpState.None,
            ApplicationService.GetFollowUpState(ApplicationStatus.Applied, null, Today));
    }

    // ---- Additional coverage: sorting ----

    private static IEnumerable<string> Roles(IEnumerable<ApplicationSummary> rows) => rows.Select(r => r.RoleTitle);

    [Theory]
    [InlineData(false, new[] { "Analyst", "Engineer", "Manager" })]
    [InlineData(true, new[] { "Manager", "Engineer", "Analyst" })]
    public async Task ListAsync_SortByRole_OrdersByRoleTitle(bool descending, string[] expected)
    {
        await _service.CreateAsync(Input(role: "Engineer"));
        await _service.CreateAsync(Input(role: "Manager"));
        await _service.CreateAsync(Input(role: "Analyst"));

        var result = await _service.ListAsync(new ApplicationFilter { SortBy = ApplicationSort.Role, Descending = descending });

        Assert.Equal(expected, Roles(result));
    }

    [Fact]
    public async Task ListAsync_SortDescendingWithTies_BreaksTiesByIdAscending()
    {
        var first = await _service.CreateAsync(Input(company: "Alpha", role: "Same"));
        var second = await _service.CreateAsync(Input(company: "Bravo", role: "Same"));

        var result = await _service.ListAsync(new ApplicationFilter { SortBy = ApplicationSort.Role, Descending = true });

        Assert.Equal([first, second], result.Select(r => r.Id));
    }

    [Theory]
    [InlineData(false, new[] { "Oldest", "Middle", "Newest" })]
    [InlineData(true, new[] { "Newest", "Middle", "Oldest" })]
    public async Task ListAsync_SortByAppliedOn_OrdersByAppliedDate(bool descending, string[] expected)
    {
        await CreateAppliedAsync("Middle", Today.AddDays(-5));
        await CreateAppliedAsync("Newest", Today.AddDays(-1));
        await CreateAppliedAsync("Oldest", Today.AddDays(-10));

        var result = await _service.ListAsync(new ApplicationFilter { SortBy = ApplicationSort.AppliedOn, Descending = descending });

        Assert.Equal(expected, Roles(result));
    }

    private async Task CreateAppliedAsync(string role, DateOnly appliedOn)
    {
        var input = Input(role: role, status: ApplicationStatus.Applied);
        input.AppliedOn = appliedOn;
        await _service.CreateAsync(input);
    }

    [Fact]
    public async Task ListAsync_SortByUpdatedAtDescending_PutsMostRecentlyEditedFirst()
    {
        var a = await _service.CreateAsync(Input(role: "A"));
        _time.Advance(TimeSpan.FromHours(1));
        await _service.CreateAsync(Input(role: "B"));
        _time.Advance(TimeSpan.FromHours(1));
        await _service.CreateAsync(Input(role: "C"));
        _time.Advance(TimeSpan.FromHours(1));
        await _service.UpdateAsync(a, Input(role: "A"));

        var result = await _service.ListAsync(new ApplicationFilter { SortBy = ApplicationSort.UpdatedAt, Descending = true });

        Assert.Equal(["A", "C", "B"], Roles(result));
    }

    [Fact]
    public async Task ListAsync_SortByFollowUpOnDescending_KeepsUndatedLast()
    {
        await _service.CreateAsync(Input(role: "None"));
        await _service.CreateAsync(Input(role: "Soon", followUpOn: Today.AddDays(1)));
        await _service.CreateAsync(Input(role: "Later", followUpOn: Today.AddDays(5)));

        var result = await _service.ListAsync(new ApplicationFilter { SortBy = ApplicationSort.FollowUpOn, Descending = true });

        Assert.Equal(["Later", "Soon", "None"], Roles(result));
    }

    // ---- Additional coverage: search and filter ----

    [Theory]
    [InlineData("a_b", 1)]      // '_' is literal, so "axb" must not match
    [InlineData(@"C:\", 1)]     // backslash (the escape char itself) is literal
    [InlineData(@"\_", 0)]      // a literal backslash followed by a literal underscore: no such text
    [InlineData("  axb  ", 1)]  // surrounding whitespace is trimmed
    public async Task ListAsync_SearchWithLikeSpecialCharacters_MatchesLiterally(string search, int expected)
    {
        await _service.CreateAsync(Input(company: "Contoso", role: "a_b tester"));
        await _service.CreateAsync(Input(company: "Fabrikam", role: "axb tester"));
        await _service.CreateAsync(Input(company: "Litware", role: @"C:\ admin"));

        var result = await _service.ListAsync(new ApplicationFilter { Search = search });

        Assert.Equal(expected, result.Count);
    }

    [Fact]
    public async Task ListAsync_StatusAndSearchCombined_AppliesBoth()
    {
        await _service.CreateAsync(Input(company: "Contoso", role: "Match", status: ApplicationStatus.Interview));
        await _service.CreateAsync(Input(company: "Contoso", role: "Wrong status", status: ApplicationStatus.Applied));
        await _service.CreateAsync(Input(company: "Fabrikam", role: "Wrong company", status: ApplicationStatus.Interview));

        var result = await _service.ListAsync(new ApplicationFilter { Status = ApplicationStatus.Interview, Search = "contoso" });

        Assert.Equal(["Match"], Roles(result));
    }

    [Fact]
    public async Task ListAsync_SearchWithNonAsciiDifferentCase_MatchesCaseInsensitively()
    {
        await _service.CreateAsync(Input(company: "Société Générale", role: "Analyst"));

        var result = await _service.ListAsync(new ApplicationFilter { Search = "SOCIÉTÉ" });

        Assert.Single(result);
    }

    // ---- Additional coverage: company reuse ----

    [Fact]
    public async Task UpdateAsync_CompanyNameChangesOnlyInCase_KeepsSameCompanyRow()
    {
        var id = await _service.CreateAsync(Input(company: "Contoso"));
        using var before = _db.CreateDbContext();
        var companyId = (await before.Applications.SingleAsync(a => a.Id == id)).CompanyId;

        await _service.UpdateAsync(id, Input(company: " CONTOSO "));

        using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.Companies.CountAsync());
        Assert.Equal(companyId, (await db.Applications.SingleAsync(a => a.Id == id)).CompanyId);
    }

    [Fact]
    public async Task UpdateAsync_CompanyMatchesAnotherExistingCompany_ReusesItIgnoringCase()
    {
        var id = await _service.CreateAsync(Input(company: "Contoso"));
        var other = await _service.CreateAsync(Input(company: "Fabrikam"));
        using var before = _db.CreateDbContext();
        var fabrikamId = (await before.Applications.SingleAsync(a => a.Id == other)).CompanyId;

        await _service.UpdateAsync(id, Input(company: "fabrikam"));

        // Contoso is left with no applications or contacts, so it is removed.
        using var db = _db.CreateDbContext();
        Assert.Equal(["Fabrikam"], await db.Companies.Select(c => c.Name).ToListAsync());
        Assert.Equal(fabrikamId, (await db.Applications.SingleAsync(a => a.Id == id)).CompanyId);
    }

    [Fact]
    public async Task CreateAsync_NonAsciiCompanyNameDifferentCase_ReusesExistingCompany()
    {
        await _service.CreateAsync(Input(company: "Société Générale"));
        await _service.CreateAsync(Input(company: "SOCIÉTÉ GÉNÉRALE", role: "Analyst II"));

        using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.Companies.CountAsync());
    }

    // ---- Additional coverage: status changes via the service ----

    [Fact]
    public async Task UpdateAsync_ToAppliedWithoutAppliedOn_SetsAppliedOnToToday()
    {
        var id = await _service.CreateAsync(Input());
        _time.Advance(TimeSpan.FromDays(2));

        await _service.UpdateAsync(id, Input(status: ApplicationStatus.Applied));

        Assert.Equal(Today.AddDays(2), (await _service.GetForEditAsync(id))!.AppliedOn);
    }

    [Fact]
    public async Task UpdateAsync_ToAppliedWithAppliedOn_KeepsGivenDate()
    {
        var id = await _service.CreateAsync(Input());
        var input = Input(status: ApplicationStatus.Applied);
        input.AppliedOn = Today.AddDays(-7);

        await _service.UpdateAsync(id, input);

        Assert.Equal(Today.AddDays(-7), (await _service.GetForEditAsync(id))!.AppliedOn);
    }

    [Fact]
    public async Task UpdateAsync_StatusUnchanged_WritesNoHistoryButUpdatesTimestamp()
    {
        var id = await _service.CreateAsync(Input(status: ApplicationStatus.Applied));
        _time.Advance(TimeSpan.FromHours(3));

        await _service.UpdateAsync(id, Input(role: "Renamed", status: ApplicationStatus.Applied));

        using var db = _db.CreateDbContext();
        var app = await db.Applications.Include(a => a.StatusHistory).SingleAsync();
        Assert.Single(app.StatusHistory);
        Assert.Equal(Start.AddHours(3).UtcDateTime, app.UpdatedAt);
    }

    [Fact]
    public async Task ChangeStatusAsync_NewStatus_PersistsUpdatedAtAndHistoryTimestamp()
    {
        var id = await _service.CreateAsync(Input());
        _time.Advance(TimeSpan.FromDays(1));

        await _service.ChangeStatusAsync(id, ApplicationStatus.Screening);

        using var db = _db.CreateDbContext();
        var app = await db.Applications.Include(a => a.StatusHistory).SingleAsync();
        var expected = Start.AddDays(1).UtcDateTime;
        Assert.Equal(expected, app.UpdatedAt);
        var change = Assert.Single(app.StatusHistory);
        Assert.Equal(ApplicationStatus.Wishlist, change.FromStatus);
        Assert.Equal(ApplicationStatus.Screening, change.ToStatus);
        Assert.Equal(expected, change.ChangedAt);
    }

    // ---- Additional coverage: delete cascade through ExecuteDelete ----

    [Fact]
    public async Task DeleteAsync_ApplicationWithChildren_CascadesAtDatabaseLevel()
    {
        var id = await _service.CreateAsync(Input(status: ApplicationStatus.Applied));
        var keepId = await _service.CreateAsync(Input(company: "Fabrikam", role: "Keep", status: ApplicationStatus.Applied));
        using (var db = _db.CreateDbContext())
        {
            var app = await db.Applications.SingleAsync(a => a.Id == id);
            app.Notes.Add(new Note { Body = "Called recruiter", CreatedAt = Start.UtcDateTime });
            app.Reminders.Add(new Reminder { Text = "Follow up", DueOn = Today.AddDays(3) });
            app.Contacts.Add(new Contact { Name = "Pat Recruiter" });
            db.Reminders.Add(new Reminder { Text = "Ad-hoc", DueOn = Today });
            await db.SaveChangesAsync();
        }

        Assert.True(await _service.DeleteAsync(id));

        using var check = _db.CreateDbContext();
        Assert.Empty(await check.Notes.ToListAsync());
        Assert.Equal([keepId], await check.StatusChanges.Select(s => s.JobApplicationId).ToListAsync());
        Assert.Equal(["Ad-hoc"], await check.Reminders.Select(r => r.Text).ToListAsync());
        Assert.Equal(1, await check.Contacts.CountAsync());
        Assert.Empty((await check.Contacts.Include(c => c.Applications).SingleAsync()).Applications);
        Assert.Equal(2, await check.Companies.CountAsync());
    }

    // ---- Additional coverage: local time zone vs UTC ----

    private static TimeZoneInfo FixedZone(int hours) =>
        TimeZoneInfo.CreateCustomTimeZone($"Test{hours:+00;-00}", TimeSpan.FromHours(hours), $"Test {hours}", $"Test {hours}");

    [Theory]
    [InlineData(1, "2026-11-21T23:30:00Z", "2026-11-22")]   // local is already tomorrow
    [InlineData(-5, "2026-11-22T00:30:00Z", "2026-11-21")]  // local is still yesterday
    [InlineData(0, "2026-11-21T23:59:59Z", "2026-11-21")]
    public void Today_UsesLocalCalendarDateNotUtc(int offsetHours, string utcNow, string expectedToday)
    {
        _time.SetUtcNow(DateTimeOffset.Parse(utcNow));
        _time.SetLocalTimeZone(FixedZone(offsetHours));

        Assert.Equal(DateOnly.Parse(expectedToday), _service.Today);
    }

    [Fact]
    public async Task ListAsync_LateUtcEveningInPlusOneZone_ComputesFollowUpAgainstLocalDate()
    {
        await _service.CreateAsync(Input(role: "UtcToday", followUpOn: new DateOnly(2026, 11, 21)));
        await _service.CreateAsync(Input(role: "LocalToday", followUpOn: new DateOnly(2026, 11, 22)));
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 21, 23, 30, 0, TimeSpan.Zero));
        _time.SetLocalTimeZone(FixedZone(1));

        var states = (await _service.ListAsync(new ApplicationFilter())).ToDictionary(a => a.RoleTitle, a => a.FollowUpState);

        Assert.Equal(FollowUpState.Overdue, states["UtcToday"]);
        Assert.Equal(FollowUpState.DueToday, states["LocalToday"]);
    }

    [Fact]
    public async Task ChangeStatusAsync_ToAppliedLateUtcEveningInPlusOneZone_SetsAppliedOnToLocalDate()
    {
        var id = await _service.CreateAsync(Input());
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 21, 23, 30, 0, TimeSpan.Zero));
        _time.SetLocalTimeZone(FixedZone(1));

        await _service.ChangeStatusAsync(id, ApplicationStatus.Applied);

        Assert.Equal(_service.Today, (await _service.GetForEditAsync(id))!.AppliedOn);
    }

    // ---- Additional coverage: length validation ----

    [Theory]
    [InlineData(nameof(ApplicationInput.Company), 201)]
    [InlineData(nameof(ApplicationInput.RoleTitle), 201)]
    [InlineData(nameof(ApplicationInput.Location), 201)]
    [InlineData(nameof(ApplicationInput.SalaryRange), 101)]
    [InlineData(nameof(ApplicationInput.Source), 101)]
    [InlineData(nameof(ApplicationInput.PostingUrl), 501)]
    public async Task CreateAsync_FieldOverMaxLength_ThrowsValidationException(string property, int length)
    {
        var input = Input();
        var value = property == nameof(ApplicationInput.PostingUrl)
            ? "https://example.com/" + new string('a', length - "https://example.com/".Length)
            : new string('x', length);
        typeof(ApplicationInput).GetProperty(property)!.SetValue(input, value);

        await Assert.ThrowsAsync<ValidationException>(() => _service.CreateAsync(input));
    }

    [Fact]
    public async Task CreateAsync_FieldsAtMaxLength_Succeeds()
    {
        var input = Input(company: new string('c', 200), role: new string('r', 200));
        input.SalaryRange = new string('s', 100);

        var id = await _service.CreateAsync(input);

        Assert.Equal(new string('r', 200), (await _service.GetForEditAsync(id))!.RoleTitle);
    }

    [Fact]
    public async Task UpdateAsync_InvalidInput_ThrowsAndLeavesRowUnchanged()
    {
        var id = await _service.CreateAsync(Input(role: "Original"));

        await Assert.ThrowsAsync<ValidationException>(() => _service.UpdateAsync(id, Input(role: new string('x', 201))));

        Assert.Equal("Original", (await _service.GetForEditAsync(id))!.RoleTitle);
    }
}
