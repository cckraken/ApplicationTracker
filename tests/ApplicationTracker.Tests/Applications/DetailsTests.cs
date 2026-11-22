using ApplicationTracker.Core.Applications;
using ApplicationTracker.Core.Contacts;
using ApplicationTracker.Core.Domain;
using ApplicationTracker.Core.Notes;
using ApplicationTracker.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace ApplicationTracker.Tests.Applications;

public sealed class DetailsTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 21);

    private readonly SqliteTestDatabase _db = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly ApplicationService _service;

    public DetailsTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _service = new ApplicationService(_db, _time);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Details_of_a_missing_application_are_null()
    {
        Assert.Null(await _service.GetDetailsAsync(999));
    }

    [Fact]
    public async Task Details_carry_the_application_fields()
    {
        var id = await _service.CreateAsync(new ApplicationInput
        {
            Company = "Fabrikam",
            RoleTitle = "Analyst",
            Status = ApplicationStatus.Applied,
            PostingUrl = "https://fabrikam.example/jobs/1",
            Location = "Leeds",
            SalaryRange = "£40k",
            Source = "Referral",
            FollowUpOn = Today.AddDays(-1),
        });

        var details = await _service.GetDetailsAsync(id);

        Assert.NotNull(details);
        Assert.Equal(
            new ApplicationDetails(id, "Analyst", "Fabrikam", ApplicationStatus.Applied,
                "https://fabrikam.example/jobs/1", "Leeds", "£40k", "Referral",
                Today, Today.AddDays(-1), FollowUpState.Overdue, 0,
                Start.UtcDateTime, Start.UtcDateTime, details.Notes, details.Contacts),
            details);
        Assert.Empty(details.Notes);
        Assert.Empty(details.Contacts);
    }

    [Fact]
    public async Task Details_count_days_in_the_current_stage()
    {
        var id = await _service.CreateAsync(new ApplicationInput { Company = "Contoso", RoleTitle = "Analyst" });
        _time.Advance(TimeSpan.FromDays(5));
        await _service.ChangeStatusAsync(id, ApplicationStatus.Applied);
        _time.Advance(TimeSpan.FromDays(2));

        Assert.Equal(2, (await _service.GetDetailsAsync(id))!.DaysInStage);
    }

    [Fact]
    public async Task Details_of_a_closed_application_have_no_follow_up_state()
    {
        var id = await _service.CreateAsync(new ApplicationInput
        {
            Company = "Contoso",
            RoleTitle = "Analyst",
            Status = ApplicationStatus.Rejected,
            FollowUpOn = Today.AddDays(-3),
        });

        Assert.Equal(FollowUpState.None, (await _service.GetDetailsAsync(id))!.FollowUpState);
    }

    [Fact]
    public async Task GetDetailsAsync_LateEveningInNewYork_UsesTheLocalDateForFollowUp()
    {
        // 22:30 on 21 November in New York is already 03:30 on the 22nd in UTC.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 22, 3, 30, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        var service = new ApplicationService(_db, time);
        var id = await service.CreateAsync(new ApplicationInput
        {
            Company = "Contoso",
            RoleTitle = "Analyst",
            Status = ApplicationStatus.Applied,
            FollowUpOn = new DateOnly(2026, 11, 21),
        });

        var details = await service.GetDetailsAsync(id);

        Assert.Equal(FollowUpState.DueToday, details!.FollowUpState);
        Assert.Equal(new DateOnly(2026, 11, 21), details.AppliedOn);
    }

    [Fact]
    public async Task GetDetailsAsync_NotesAndContactsOfOtherApplications_AreNotIncluded()
    {
        var notes = new NoteService(_db, _time);
        var contacts = new ContactService(_db);
        var mine = await _service.CreateAsync(new ApplicationInput { Company = "Contoso", RoleTitle = "Analyst" });
        var other = await _service.CreateAsync(new ApplicationInput { Company = "Fabrikam", RoleTitle = "Engineer" });
        await notes.AddAsync(mine, "Mine");
        await notes.AddAsync(other, "Other");
        var shared = await contacts.CreateForApplicationAsync(mine, new ContactInput { Name = "Shared" });
        await contacts.LinkAsync(other, shared!.Value);
        await contacts.CreateForApplicationAsync(other, new ContactInput { Name = "Only other" });

        var details = await _service.GetDetailsAsync(mine);

        Assert.Equal(["Mine"], details!.Notes.Select(n => n.Body));
        Assert.Equal(["Shared"], details.Contacts.Select(c => c.Name));
        Assert.Equal(["Only other", "Shared"], (await _service.GetDetailsAsync(other))!.Contacts.Select(c => c.Name));
    }

    [Fact]
    public async Task GetDetailsAsync_ContactWithoutCompany_HasNullCompanyName()
    {
        var contacts = new ContactService(_db);
        var id = await _service.CreateAsync(new ApplicationInput { Company = "Contoso", RoleTitle = "Analyst" });
        await contacts.CreateForApplicationAsync(id, new ContactInput { Name = "Freelance Recruiter" });

        Assert.Null(Assert.Single((await _service.GetDetailsAsync(id))!.Contacts).CompanyName);
    }

    [Fact]
    public async Task GetDetailsAsync_NoteTimestampsReadBack_AreUtc()
    {
        var notes = new NoteService(_db, _time);
        var id = await _service.CreateAsync(new ApplicationInput { Company = "Contoso", RoleTitle = "Analyst" });
        var note = await notes.AddAsync(id, "Draft");
        _time.Advance(TimeSpan.FromMinutes(10));
        await notes.UpdateAsync(note!.Id, "Final");

        var details = await _service.GetDetailsAsync(id);

        var read = Assert.Single(details!.Notes);
        Assert.Equal(DateTimeKind.Utc, read.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, read.UpdatedAt!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, details.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, details.UpdatedAt.Kind);
    }
}
