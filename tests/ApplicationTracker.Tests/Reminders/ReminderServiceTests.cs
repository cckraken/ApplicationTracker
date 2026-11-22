using System.ComponentModel.DataAnnotations;
using System.Globalization;
using ApplicationTracker.Core.Applications;
using ApplicationTracker.Core.Domain;
using ApplicationTracker.Core.Reminders;
using ApplicationTracker.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace ApplicationTracker.Tests.Reminders;

public sealed class ReminderServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 21);

    private readonly SqliteTestDatabase _db = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly ReminderService _service;
    private readonly ApplicationService _applications;

    public ReminderServiceTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _service = new ReminderService(_db, _time);
        _applications = new ApplicationService(_db, _time);
    }

    public void Dispose() => _db.Dispose();

    // ---- Helpers ----

    private static ReminderInput Input(string text = "Call the recruiter", DateOnly? dueOn = null, int? applicationId = null) =>
        new() { Text = text, DueOn = dueOn ?? Today, ApplicationId = applicationId };

    private Task<int> Add(string text = "Call the recruiter", DateOnly? dueOn = null, int? applicationId = null) =>
        _service.AddAsync(Input(text, dueOn, applicationId));

    private Task<int> CreateApplication(
        string company = "Contoso", string role = "Data Analyst", ApplicationStatus status = ApplicationStatus.Applied) =>
        _applications.CreateAsync(new ApplicationInput { Company = company, RoleTitle = role, Status = status });

    private async Task<Reminder> Load(int id)
    {
        using var db = _db.CreateDbContext();
        return await db.Reminders.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private async Task<bool> Exists(int id)
    {
        using var db = _db.CreateDbContext();
        return await db.Reminders.AnyAsync(r => r.Id == id);
    }

    private static TimeZoneInfo FixedZone(int hours) =>
        TimeZoneInfo.CreateCustomTimeZone($"Test{hours:+00;-00}", TimeSpan.FromHours(hours), $"Test {hours}", $"Test {hours}");

    private static TimeZoneInfo Zone(string ianaId) => TimeZoneInfo.FindSystemTimeZoneById(ianaId);

    /// <summary>A service whose clock starts at <paramref name="utcNow"/> in <paramref name="zone"/>.</summary>
    private ReminderService ServiceAt(DateTimeOffset utcNow, TimeZoneInfo zone)
    {
        var time = new FakeTimeProvider(utcNow);
        time.SetLocalTimeZone(zone);
        return new ReminderService(_db, time);
    }

    private static DateOnly Date(string value) => DateOnly.Parse(value, CultureInfo.InvariantCulture);

    private static DateTimeOffset Utc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    // ---- Add ----

    [Fact]
    public async Task Add_trims_the_text_and_records_the_creation_time_in_utc()
    {
        var id = await Add("  Send thank-you email \n", Today.AddDays(2));

        var saved = await Load(id);
        Assert.Equal("Send thank-you email", saved.Text);
        Assert.Equal(Today.AddDays(2), saved.DueOn);
        Assert.Null(saved.JobApplicationId);
        Assert.False(saved.IsDone);
        Assert.Null(saved.CompletedAt);
        Assert.Equal(Start.UtcDateTime, saved.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, saved.CreatedAt.Kind);
    }

    [Fact]
    public async Task Add_links_the_reminder_to_the_chosen_application()
    {
        var appId = await CreateApplication("Fabrikam", "Analyst");

        var id = await Add(applicationId: appId);

        var view = Assert.Single((await _service.ListAsync(new ReminderFilter())).Open);
        Assert.Equal(id, view.Id);
        Assert.Equal(new ReminderApplication(appId, "Analyst", "Fabrikam"), view.Application);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task Add_rejects_empty_or_whitespace_text(string text)
    {
        await Assert.ThrowsAsync<ValidationException>(() => _service.AddAsync(Input(text)));

        Assert.Empty((await _service.ListAsync(new ReminderFilter())).Open);
    }

    [Fact]
    public async Task Add_rejects_a_missing_due_date()
    {
        var input = new ReminderInput { Text = "No date" };

        await Assert.ThrowsAsync<ValidationException>(() => _service.AddAsync(input));
    }

    [Fact]
    public async Task Add_rejects_text_longer_than_the_maximum()
    {
        var input = Input(new string('a', Reminder.MaxTextLength + 1));

        await Assert.ThrowsAsync<ValidationException>(() => _service.AddAsync(input));
    }

    [Fact]
    public async Task Add_accepts_text_of_exactly_the_maximum_length()
    {
        var text = new string('a', Reminder.MaxTextLength);

        var id = await Add(text);

        Assert.Equal(text, (await Load(id)).Text);
    }

    [Fact]
    public async Task Add_rejects_an_application_that_does_not_exist()
    {
        await Assert.ThrowsAsync<ApplicationNotFoundException>(() => _service.AddAsync(Input(applicationId: 999)));

        using var db = _db.CreateDbContext();
        Assert.False(await db.Reminders.AnyAsync());
    }

    // ---- GetForEdit ----

    [Fact]
    public async Task GetForEdit_returns_the_current_values()
    {
        var appId = await CreateApplication();
        var id = await Add("Prepare questions", Today.AddDays(3), appId);

        var input = await _service.GetForEditAsync(id);

        Assert.NotNull(input);
        Assert.Equal("Prepare questions", input.Text);
        Assert.Equal(Today.AddDays(3), input.DueOn);
        Assert.Equal(appId, input.ApplicationId);
    }

    [Fact]
    public async Task GetForEdit_returns_null_for_a_missing_reminder()
    {
        Assert.Null(await _service.GetForEditAsync(999));
    }

    // ---- Update ----

    [Fact]
    public async Task Update_replaces_text_due_date_and_application_and_trims_the_text()
    {
        var firstApp = await CreateApplication("Contoso");
        var secondApp = await CreateApplication("Fabrikam");
        var id = await Add("Old", Today, firstApp);

        var updated = await _service.UpdateAsync(id, Input("  New text  ", Today.AddDays(5), secondApp));

        Assert.True(updated);
        var saved = await Load(id);
        Assert.Equal("New text", saved.Text);
        Assert.Equal(Today.AddDays(5), saved.DueOn);
        Assert.Equal(secondApp, saved.JobApplicationId);
    }

    [Fact]
    public async Task Update_can_unlink_the_application()
    {
        var appId = await CreateApplication();
        var id = await Add(applicationId: appId);

        await _service.UpdateAsync(id, Input(applicationId: null));

        Assert.Null((await Load(id)).JobApplicationId);
    }

    [Fact]
    public async Task Update_keeps_the_done_state_and_completion_time()
    {
        var id = await Add();
        await _service.CompleteAsync(id);
        _time.Advance(TimeSpan.FromHours(2));

        await _service.UpdateAsync(id, Input("Edited after done", Today.AddDays(1)));

        var saved = await Load(id);
        Assert.True(saved.IsDone);
        Assert.Equal(Start.UtcDateTime, saved.CompletedAt);
        Assert.Equal("Edited after done", saved.Text);
    }

    [Fact]
    public async Task Update_keeps_the_creation_time()
    {
        var id = await Add();
        _time.Advance(TimeSpan.FromDays(1));

        await _service.UpdateAsync(id, Input("Edited"));

        Assert.Equal(Start.UtcDateTime, (await Load(id)).CreatedAt);
    }

    [Fact]
    public async Task Update_returns_false_for_a_missing_reminder()
    {
        Assert.False(await _service.UpdateAsync(999, Input()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Update_rejects_empty_or_whitespace_text_and_keeps_the_old_values(string text)
    {
        var id = await Add("Keep me");

        await Assert.ThrowsAsync<ValidationException>(() => _service.UpdateAsync(id, Input(text)));

        Assert.Equal("Keep me", (await Load(id)).Text);
    }

    [Fact]
    public async Task Update_rejects_a_missing_due_date()
    {
        var id = await Add();

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.UpdateAsync(id, new ReminderInput { Text = "No date" }));
    }

    [Fact]
    public async Task Update_rejects_text_longer_than_the_maximum()
    {
        var id = await Add();

        await Assert.ThrowsAsync<ValidationException>(
            () => _service.UpdateAsync(id, Input(new string('a', Reminder.MaxTextLength + 1))));
    }

    [Fact]
    public async Task Update_rejects_an_application_that_does_not_exist_and_keeps_the_old_link()
    {
        var appId = await CreateApplication();
        var id = await Add(applicationId: appId);

        await Assert.ThrowsAsync<ApplicationNotFoundException>(() => _service.UpdateAsync(id, Input(applicationId: 999)));

        Assert.Equal(appId, (await Load(id)).JobApplicationId);
    }

    // ---- Complete / Reopen ----

    [Fact]
    public async Task Complete_marks_the_reminder_done_at_the_current_utc_time()
    {
        var id = await Add();
        _time.Advance(TimeSpan.FromMinutes(45));

        Assert.True(await _service.CompleteAsync(id));

        var saved = await Load(id);
        Assert.True(saved.IsDone);
        Assert.Equal(Start.UtcDateTime.AddMinutes(45), saved.CompletedAt);
        Assert.Equal(DateTimeKind.Utc, saved.CompletedAt!.Value.Kind);
    }

    [Fact]
    public async Task Completing_twice_keeps_the_first_completion_time()
    {
        var id = await Add();
        await _service.CompleteAsync(id);
        _time.Advance(TimeSpan.FromHours(3));

        Assert.True(await _service.CompleteAsync(id));

        Assert.Equal(Start.UtcDateTime, (await Load(id)).CompletedAt);
    }

    [Fact]
    public async Task Complete_returns_false_for_a_missing_reminder()
    {
        Assert.False(await _service.CompleteAsync(999));
    }

    [Fact]
    public async Task Reopen_makes_a_done_reminder_open_with_its_old_due_date()
    {
        var id = await Add(dueOn: Today.AddDays(-3));
        await _service.CompleteAsync(id);

        Assert.True(await _service.ReopenAsync(id));

        var saved = await Load(id);
        Assert.False(saved.IsDone);
        Assert.Null(saved.CompletedAt);
        Assert.Equal(Today.AddDays(-3), saved.DueOn);
    }

    [Fact]
    public async Task Reopen_on_an_open_reminder_changes_nothing()
    {
        var id = await Add(dueOn: Today.AddDays(1));

        Assert.True(await _service.ReopenAsync(id));

        var saved = await Load(id);
        Assert.False(saved.IsDone);
        Assert.Equal(Today.AddDays(1), saved.DueOn);
    }

    [Fact]
    public async Task Reopen_returns_false_for_a_missing_reminder()
    {
        Assert.False(await _service.ReopenAsync(999));
    }

    // ---- Snooze ----

    [Theory]
    [InlineData(SnoozePeriod.Tomorrow, -5, 1)]
    [InlineData(SnoozePeriod.NextWeek, -5, 7)]
    [InlineData(SnoozePeriod.Tomorrow, 0, 1)]
    [InlineData(SnoozePeriod.NextWeek, 0, 7)]
    [InlineData(SnoozePeriod.Tomorrow, 30, 1)]  // counted from today, so a far-off date moves earlier
    [InlineData(SnoozePeriod.NextWeek, 30, 7)]
    public async Task Snooze_counts_from_today_not_from_the_old_due_date(SnoozePeriod period, int dueOffset, int expectedOffset)
    {
        var id = await Add(dueOn: Today.AddDays(dueOffset));

        Assert.Equal(Today.AddDays(expectedOffset), await _service.SnoozeAsync(id, period));

        Assert.Equal(Today.AddDays(expectedOffset), (await Load(id)).DueOn);
    }

    [Fact]
    public async Task Snoozing_a_done_reminder_reopens_it()
    {
        var id = await Add(dueOn: Today.AddDays(-1));
        await _service.CompleteAsync(id);

        await _service.SnoozeAsync(id, SnoozePeriod.Tomorrow);

        var saved = await Load(id);
        Assert.False(saved.IsDone);
        Assert.Null(saved.CompletedAt);
        Assert.Equal(Today.AddDays(1), saved.DueOn);
    }

    [Fact]
    public async Task Snooze_returns_false_for_a_missing_reminder()
    {
        Assert.Null(await _service.SnoozeAsync(999, SnoozePeriod.Tomorrow));
    }

    [Theory]
    // UTC+1 at 23:30 UTC on the 21st is already the 22nd locally.
    [InlineData("2026-11-21T23:30:00", 1, SnoozePeriod.Tomorrow, "2026-11-23")]
    [InlineData("2026-11-21T23:30:00", 1, SnoozePeriod.NextWeek, "2026-11-29")]
    // UTC-5 at 03:00 UTC on the 21st is still 22:00 on the 20th locally.
    [InlineData("2026-11-21T03:00:00", -5, SnoozePeriod.Tomorrow, "2026-11-21")]
    [InlineData("2026-11-21T03:00:00", -5, SnoozePeriod.NextWeek, "2026-11-27")]
    public async Task Snooze_late_evening_or_early_morning_counts_from_the_local_date(
        string utcNow, int offsetHours, SnoozePeriod period, string expected)
    {
        var id = await Add(dueOn: new DateOnly(2026, 11, 1));
        var service = ServiceAt(Utc(utcNow), FixedZone(offsetHours));

        await service.SnoozeAsync(id, period);

        Assert.Equal(Date(expected), (await Load(id)).DueOn);
    }

    [Theory]
    // Autumn change (25 Oct 2026): 23:30 UTC is 23:30 GMT, still the 25th.
    [InlineData("Europe/London", "2026-10-25T23:30:00", "2026-10-26")]
    // Spring change (29 Mar 2026): 23:30 UTC is 00:30 BST on the 30th.
    [InlineData("Europe/London", "2026-03-29T23:30:00", "2026-03-31")]
    // New York spring change (8 Mar 2026): 03:30 UTC on the 9th is 23:30 EDT on the 8th.
    [InlineData("America/New_York", "2026-03-09T03:30:00", "2026-03-09")]
    public async Task Snooze_on_a_dst_change_day_uses_the_local_date(string zoneId, string utcNow, string expectedTomorrow)
    {
        var id = await Add(dueOn: new DateOnly(2026, 1, 1));
        var service = ServiceAt(Utc(utcNow), Zone(zoneId));

        await service.SnoozeAsync(id, SnoozePeriod.Tomorrow);

        Assert.Equal(Date(expectedTomorrow), (await Load(id)).DueOn);
    }

    // ---- Delete ----

    [Fact]
    public async Task Delete_removes_the_reminder()
    {
        var keep = await Add("Keep");
        var id = await Add("Delete me");

        Assert.True(await _service.DeleteAsync(id));

        Assert.False(await Exists(id));
        Assert.True(await Exists(keep));
    }

    [Fact]
    public async Task Delete_returns_false_for_a_missing_reminder()
    {
        Assert.False(await _service.DeleteAsync(999));
    }

    [Fact]
    public async Task Deleting_the_application_deletes_its_reminders_but_not_others()
    {
        var appId = await CreateApplication("Contoso");
        var otherApp = await CreateApplication("Fabrikam");
        var linked = await Add("Linked", applicationId: appId);
        var otherLinked = await Add("Other", applicationId: otherApp);
        var standalone = await Add("Standalone");

        await _applications.DeleteAsync(appId);

        Assert.False(await Exists(linked));
        Assert.True(await Exists(otherLinked));
        Assert.True(await Exists(standalone));
    }

    // ---- List ----

    [Fact]
    public async Task List_orders_open_reminders_by_due_date_then_id()
    {
        var later = await Add("Later", Today.AddDays(3));
        var todayFirst = await Add("Today first", Today);
        var overdue = await Add("Overdue", Today.AddDays(-2));
        var todaySecond = await Add("Today second", Today);

        var list = await _service.ListAsync(new ReminderFilter());

        Assert.Equal([overdue, todayFirst, todaySecond, later], list.Open.Select(r => r.Id));
    }

    [Fact]
    public async Task List_orders_done_reminders_by_completion_time_latest_first_then_id_descending()
    {
        var first = await Add("First");
        var sameTimeA = await Add("Same time A");
        var sameTimeB = await Add("Same time B");
        await _service.CompleteAsync(first);
        _time.Advance(TimeSpan.FromHours(1));
        await _service.CompleteAsync(sameTimeA);
        await _service.CompleteAsync(sameTimeB);

        var list = await _service.ListAsync(new ReminderFilter());

        Assert.Empty(list.Open);
        Assert.Equal([sameTimeB, sameTimeA, first], list.Done.Select(r => r.Id));
    }

    [Fact]
    public async Task List_returns_at_most_DoneLimit_done_reminders_most_recent_first()
    {
        var ids = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            var id = await Add($"Done {i}");
            await _service.CompleteAsync(id);
            ids.Add(id);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        var list = await _service.ListAsync(new ReminderFilter { DoneLimit = 2 });

        Assert.Equal([ids[3], ids[2]], list.Done.Select(r => r.Id));
    }

    [Fact]
    public async Task List_returns_ten_done_reminders_by_default()
    {
        for (var i = 0; i < 12; i++)
        {
            await _service.CompleteAsync(await Add($"Done {i}"));
        }

        var list = await _service.ListAsync(new ReminderFilter());

        Assert.Equal(10, list.Done.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task List_with_no_done_limit_returns_no_done_reminders(int limit)
    {
        await _service.CompleteAsync(await Add("Done"));
        var open = await Add("Open");

        var list = await _service.ListAsync(new ReminderFilter { DoneLimit = limit });

        Assert.Empty(list.Done);
        Assert.Equal([open], list.Open.Select(r => r.Id));
    }

    [Fact]
    public async Task List_filters_by_application()
    {
        var appId = await CreateApplication("Contoso");
        var otherApp = await CreateApplication("Fabrikam");
        var openLinked = await Add("Open linked", applicationId: appId);
        var doneLinked = await Add("Done linked", applicationId: appId);
        await _service.CompleteAsync(doneLinked);
        await Add("Other app", applicationId: otherApp);
        await _service.CompleteAsync(await Add("Standalone done"));
        await Add("Standalone");

        var list = await _service.ListAsync(new ReminderFilter { ApplicationId = appId });

        Assert.Equal([openLinked], list.Open.Select(r => r.Id));
        Assert.Equal([doneLinked], list.Done.Select(r => r.Id));
    }

    [Fact]
    public async Task List_without_an_application_filter_includes_linked_and_standalone_reminders()
    {
        var appId = await CreateApplication();
        var linked = await Add("Linked", Today, appId);
        var standalone = await Add("Standalone", Today.AddDays(1));

        var list = await _service.ListAsync(new ReminderFilter());

        Assert.Equal([linked, standalone], list.Open.Select(r => r.Id));
        Assert.Null(list.Open[1].Application);
    }

    [Fact]
    public async Task List_views_carry_state_relative_to_today_and_none_once_done()
    {
        var overdue = await Add("Overdue", Today.AddDays(-1));
        var dueToday = await Add("Today", Today);
        var upcoming = await Add("Upcoming", Today.AddDays(1));
        var done = await Add("Done", Today.AddDays(-1));
        await _service.CompleteAsync(done);

        var list = await _service.ListAsync(new ReminderFilter());

        var states = list.Open.ToDictionary(r => r.Id, r => r.State);
        Assert.Equal(FollowUpState.Overdue, states[overdue]);
        Assert.Equal(FollowUpState.DueToday, states[dueToday]);
        Assert.Equal(FollowUpState.Upcoming, states[upcoming]);
        var doneView = Assert.Single(list.Done);
        Assert.Equal(FollowUpState.None, doneView.State);
        Assert.True(doneView.IsDone);
        Assert.Equal(Start.UtcDateTime, doneView.CompletedAt);
    }

    [Fact]
    public async Task DueOnly_returns_overdue_and_due_today_open_reminders_and_no_done_ones()
    {
        var overdue = await Add("Overdue", Today.AddDays(-1));
        var dueToday = await Add("Today", Today);
        await Add("Tomorrow", Today.AddDays(1));
        await _service.CompleteAsync(await Add("Done overdue", Today.AddDays(-1)));

        var list = await _service.ListAsync(new ReminderFilter { DueOnly = true });

        Assert.Equal([overdue, dueToday], list.Open.Select(r => r.Id));
        Assert.Empty(list.Done);
    }

    [Fact]
    public async Task DueOnly_combines_with_the_application_filter()
    {
        var appId = await CreateApplication();
        var linkedDue = await Add("Linked due", Today, appId);
        await Add("Linked later", Today.AddDays(2), appId);
        await Add("Standalone due", Today);

        var list = await _service.ListAsync(new ReminderFilter { ApplicationId = appId, DueOnly = true });

        Assert.Equal([linkedDue], list.Open.Select(r => r.Id));
    }

    [Theory]
    // UTC+1 at 23:30 UTC on the 21st: local today is the 22nd.
    [InlineData("2026-11-21T23:30:00", 1, "2026-11-22", true, FollowUpState.DueToday)]
    [InlineData("2026-11-21T23:30:00", 1, "2026-11-21", true, FollowUpState.Overdue)]
    [InlineData("2026-11-21T23:30:00", 1, "2026-11-23", false, FollowUpState.Upcoming)]
    // UTC-5 at 03:00 UTC on the 21st: local today is still the 20th.
    [InlineData("2026-11-21T03:00:00", -5, "2026-11-20", true, FollowUpState.DueToday)]
    [InlineData("2026-11-21T03:00:00", -5, "2026-11-21", false, FollowUpState.Upcoming)]
    // UTC+9 at 15:30 UTC on the 21st: local today is the 22nd (early morning locally).
    [InlineData("2026-11-21T15:30:00", 9, "2026-11-22", true, FollowUpState.DueToday)]
    public async Task DueOnly_uses_the_local_date_when_it_differs_from_the_utc_date(
        string utcNow, int offsetHours, string dueOn, bool expectIncluded, FollowUpState expectedState)
    {
        var id = await Add(dueOn: Date(dueOn));
        var service = ServiceAt(Utc(utcNow), FixedZone(offsetHours));

        var dueOnly = await service.ListAsync(new ReminderFilter { DueOnly = true });
        var all = await service.ListAsync(new ReminderFilter());

        Assert.Equal(expectIncluded, dueOnly.Open.Any(r => r.Id == id));
        Assert.Equal(expectedState, all.Open.Single(r => r.Id == id).State);
    }

    [Theory]
    // Spring change in London (29 Mar 2026): 23:30 UTC is 00:30 BST on the 30th.
    [InlineData("Europe/London", "2026-03-29T23:30:00", "2026-03-30", true)]
    // Autumn change in London (25 Oct 2026): 23:30 UTC is 23:30 GMT, still the 25th.
    [InlineData("Europe/London", "2026-10-25T23:30:00", "2026-10-26", false)]
    [InlineData("Europe/London", "2026-10-25T23:30:00", "2026-10-25", true)]
    public async Task DueOnly_on_a_dst_change_day_uses_the_local_date(string zoneId, string utcNow, string dueOn, bool expectIncluded)
    {
        var id = await Add(dueOn: Date(dueOn));
        var service = ServiceAt(Utc(utcNow), Zone(zoneId));

        var list = await service.ListAsync(new ReminderFilter { DueOnly = true });

        Assert.Equal(expectIncluded, list.Open.Any(r => r.Id == id));
    }

    [Theory]
    [InlineData(1, "2026-11-21T23:30:00", "2026-11-22")]
    [InlineData(-5, "2026-11-21T03:00:00", "2026-11-20")]
    [InlineData(0, "2026-11-21T23:59:59", "2026-11-21")]
    public void Today_is_the_local_calendar_date(int offsetHours, string utcNow, string expected)
    {
        var service = ServiceAt(Utc(utcNow), FixedZone(offsetHours));

        Assert.Equal(Date(expected), service.Today);
    }

    // ---- GetState ----

    [Theory]
    [InlineData(-1, false, FollowUpState.Overdue)]
    [InlineData(0, false, FollowUpState.DueToday)]
    [InlineData(1, false, FollowUpState.Upcoming)]
    [InlineData(-1, true, FollowUpState.None)]
    [InlineData(0, true, FollowUpState.None)]
    [InlineData(1, true, FollowUpState.None)]
    public void GetState_is_relative_to_today_and_none_when_done(int dueOffset, bool isDone, FollowUpState expected)
    {
        Assert.Equal(expected, ReminderService.GetState(Today.AddDays(dueOffset), isDone, Today));
    }

    // ---- Application choices ----

    [Fact]
    public async Task Application_choices_list_open_applications_first_then_closed_each_by_company_then_role()
    {
        await CreateApplication("Fabrikam", "Writer", ApplicationStatus.Rejected);
        await CreateApplication("Fabrikam", "Analyst", ApplicationStatus.Interview);
        await CreateApplication("Contoso", "Tester", ApplicationStatus.Withdrawn);
        await CreateApplication("Contoso", "Developer", ApplicationStatus.Wishlist);
        await CreateApplication("Adatum", "Manager", ApplicationStatus.Offer);

        var choices = await _service.GetApplicationChoicesAsync();

        Assert.Equal(
            [("Adatum", "Manager", false), ("Contoso", "Developer", false), ("Fabrikam", "Analyst", false),
             ("Contoso", "Tester", true), ("Fabrikam", "Writer", true)],
            choices.Select(c => (c.CompanyName, c.RoleTitle, c.IsClosed)));
    }

    [Fact]
    public async Task Application_choices_sort_names_case_insensitively_including_non_ascii_letters()
    {
        // Binary order would put "ÉCLAIR" (U+00C9) before "ébène" (U+00E9) and "Zeta" before "alpha".
        await CreateApplication("ÉCLAIR", "Role");
        await CreateApplication("ébène", "Role");
        await CreateApplication("Zeta", "Role");
        await CreateApplication("alpha", "Role");
        await CreateApplication("Acme", "ÅNGSTRÖM ARCHITECT");
        await CreateApplication("Acme", "Ångström analyst");

        var order = (await _service.GetApplicationChoicesAsync()).Select(c => $"{c.CompanyName}/{c.RoleTitle}").ToList();

        // Only case is compared here; where accented letters fall relative to A-Z is not specified.
        Assert.True(order.IndexOf("alpha/Role") < order.IndexOf("Zeta/Role"));
        Assert.True(order.IndexOf("ébène/Role") < order.IndexOf("ÉCLAIR/Role"));
        Assert.True(order.IndexOf("Acme/Ångström analyst") < order.IndexOf("Acme/ÅNGSTRÖM ARCHITECT"));
    }

    [Fact]
    public async Task Application_choices_carry_the_application_id()
    {
        var id = await CreateApplication("Contoso", "Analyst");

        var choice = Assert.Single(await _service.GetApplicationChoicesAsync());

        Assert.Equal(new ApplicationChoice(id, "Analyst", "Contoso", false), choice);
    }

    [Fact]
    public async Task Application_choices_are_empty_without_applications()
    {
        Assert.Empty(await _service.GetApplicationChoicesAsync());
    }
}
