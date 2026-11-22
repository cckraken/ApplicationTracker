using System.Globalization;
using ApplicationTracker.Core.Applications;
using ApplicationTracker.Core.Domain;
using ApplicationTracker.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace ApplicationTracker.Tests.Applications;

public sealed class FollowUpActionTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 21);

    private readonly SqliteTestDatabase _db = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly ApplicationService _service;

    public FollowUpActionTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _service = new ApplicationService(_db, _time);
    }

    public void Dispose() => _db.Dispose();

    private Task<int> Create(DateOnly? followUpOn, ApplicationStatus status = ApplicationStatus.Applied) =>
        _service.CreateAsync(new ApplicationInput
        {
            Company = "Contoso",
            RoleTitle = "Data Analyst",
            Status = status,
            FollowUpOn = followUpOn,
        });

    private async Task<ApplicationDetails> Details(int id) => (await _service.GetDetailsAsync(id))!;

    private ApplicationService ServiceAt(DateTimeOffset utcNow, TimeZoneInfo zone)
    {
        var time = new FakeTimeProvider(utcNow);
        time.SetLocalTimeZone(zone);
        return new ApplicationService(_db, time);
    }

    private static TimeZoneInfo FixedZone(int hours) =>
        TimeZoneInfo.CreateCustomTimeZone($"Test{hours:+00;-00}", TimeSpan.FromHours(hours), $"Test {hours}", $"Test {hours}");

    private static DateOnly Date(string value) => DateOnly.Parse(value, CultureInfo.InvariantCulture);

    private static DateTimeOffset Utc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    // ---- SnoozePeriod.DueFrom ----

    [Theory]
    [InlineData(SnoozePeriod.Tomorrow, "2026-11-21", "2026-11-22")]
    [InlineData(SnoozePeriod.NextWeek, "2026-11-21", "2026-11-28")]
    [InlineData(SnoozePeriod.Tomorrow, "2026-12-31", "2027-01-01")]
    [InlineData(SnoozePeriod.NextWeek, "2026-12-28", "2027-01-04")]
    [InlineData(SnoozePeriod.Tomorrow, "2028-02-28", "2028-02-29")]
    public void DueFrom_counts_calendar_days_from_today(SnoozePeriod period, string today, string expected)
    {
        Assert.Equal(Date(expected), period.DueFrom(Date(today)));
    }

    [Fact]
    public void DueFrom_rejects_an_unknown_period()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((SnoozePeriod)99).DueFrom(Today));
    }

    // ---- CompleteFollowUpAsync ----

    [Fact]
    public async Task Completing_a_follow_up_clears_the_date_and_sets_UpdatedAt()
    {
        var id = await Create(Today.AddDays(-1));
        _time.Advance(TimeSpan.FromHours(2));

        var outcome = await _service.CompleteFollowUpAsync(id, Today.AddDays(-1));

        Assert.Equal(FollowUpActionOutcome.Done, outcome);
        var details = await Details(id);
        Assert.Null(details.FollowUpOn);
        Assert.Equal(FollowUpState.None, details.FollowUpState);
        Assert.Equal(Start.UtcDateTime.AddHours(2), details.UpdatedAt);
    }

    [Fact]
    public async Task Completing_a_follow_up_whose_date_changed_elsewhere_saves_nothing()
    {
        var id = await Create(Today.AddDays(3));
        var updatedBefore = (await Details(id)).UpdatedAt;
        _time.Advance(TimeSpan.FromHours(2));

        var outcome = await _service.CompleteFollowUpAsync(id, Today);

        Assert.Equal(FollowUpActionOutcome.Changed, outcome);
        var details = await Details(id);
        Assert.Equal(Today.AddDays(3), details.FollowUpOn);
        Assert.Equal(updatedBefore, details.UpdatedAt);
    }

    [Fact]
    public async Task Completing_a_follow_up_that_was_already_cleared_reports_changed()
    {
        var id = await Create(followUpOn: null);

        Assert.Equal(FollowUpActionOutcome.Changed, await _service.CompleteFollowUpAsync(id, Today));
        Assert.Null((await Details(id)).FollowUpOn);
    }

    [Fact]
    public async Task Completing_a_follow_up_twice_reports_changed_the_second_time()
    {
        var id = await Create(Today);

        Assert.Equal(FollowUpActionOutcome.Done, await _service.CompleteFollowUpAsync(id, Today));
        Assert.Equal(FollowUpActionOutcome.Changed, await _service.CompleteFollowUpAsync(id, Today));
    }

    [Fact]
    public async Task Completing_the_follow_up_of_a_missing_application_reports_not_found()
    {
        Assert.Equal(FollowUpActionOutcome.NotFound, await _service.CompleteFollowUpAsync(999, Today));
    }

    [Fact]
    public async Task Completing_a_follow_up_does_not_change_the_status()
    {
        var id = await Create(Today, ApplicationStatus.Interview);

        await _service.CompleteFollowUpAsync(id, Today);

        Assert.Equal(ApplicationStatus.Interview, (await Details(id)).Status);
    }

    // ---- SnoozeFollowUpAsync ----

    [Theory]
    [InlineData(SnoozePeriod.Tomorrow, -4, 1)]
    [InlineData(SnoozePeriod.NextWeek, -4, 7)]
    [InlineData(SnoozePeriod.Tomorrow, 0, 1)]
    [InlineData(SnoozePeriod.NextWeek, 0, 7)]
    [InlineData(SnoozePeriod.Tomorrow, 20, 1)]
    public async Task Snoozing_a_follow_up_moves_it_from_today_not_from_the_old_date(
        SnoozePeriod period, int followUpOffset, int expectedOffset)
    {
        var id = await Create(Today.AddDays(followUpOffset));

        var outcome = await _service.SnoozeFollowUpAsync(id, Today.AddDays(followUpOffset), period);

        Assert.Equal(FollowUpActionOutcome.Done, outcome);
        Assert.Equal(Today.AddDays(expectedOffset), (await Details(id)).FollowUpOn);
    }

    [Fact]
    public async Task Snoozing_a_follow_up_sets_UpdatedAt()
    {
        var id = await Create(Today);
        _time.Advance(TimeSpan.FromMinutes(30));

        await _service.SnoozeFollowUpAsync(id, Today, SnoozePeriod.Tomorrow);

        Assert.Equal(Start.UtcDateTime.AddMinutes(30), (await Details(id)).UpdatedAt);
    }

    [Fact]
    public async Task Snoozing_a_follow_up_whose_date_changed_elsewhere_saves_nothing()
    {
        var id = await Create(Today.AddDays(2));
        var updatedBefore = (await Details(id)).UpdatedAt;
        _time.Advance(TimeSpan.FromHours(1));

        var outcome = await _service.SnoozeFollowUpAsync(id, Today, SnoozePeriod.NextWeek);

        Assert.Equal(FollowUpActionOutcome.Changed, outcome);
        var details = await Details(id);
        Assert.Equal(Today.AddDays(2), details.FollowUpOn);
        Assert.Equal(updatedBefore, details.UpdatedAt);
    }

    [Fact]
    public async Task Snoozing_a_cleared_follow_up_reports_changed_and_sets_no_date()
    {
        var id = await Create(followUpOn: null);

        Assert.Equal(FollowUpActionOutcome.Changed, await _service.SnoozeFollowUpAsync(id, Today, SnoozePeriod.Tomorrow));
        Assert.Null((await Details(id)).FollowUpOn);
    }

    [Fact]
    public async Task Snoozing_the_follow_up_of_a_missing_application_reports_not_found()
    {
        Assert.Equal(FollowUpActionOutcome.NotFound, await _service.SnoozeFollowUpAsync(999, Today, SnoozePeriod.Tomorrow));
    }

    [Theory]
    // UTC+1 at 23:30 UTC on the 21st: local today is the 22nd.
    [InlineData("2026-11-21T23:30:00", 1, SnoozePeriod.Tomorrow, "2026-11-23")]
    [InlineData("2026-11-21T23:30:00", 1, SnoozePeriod.NextWeek, "2026-11-29")]
    // UTC-5 at 03:00 UTC on the 21st: local today is still the 20th.
    [InlineData("2026-11-21T03:00:00", -5, SnoozePeriod.Tomorrow, "2026-11-21")]
    [InlineData("2026-11-21T03:00:00", -5, SnoozePeriod.NextWeek, "2026-11-27")]
    public async Task Snoozing_late_evening_or_early_morning_counts_from_the_local_date(
        string utcNow, int offsetHours, SnoozePeriod period, string expected)
    {
        var followUp = new DateOnly(2026, 11, 10);
        var id = await Create(followUp);
        var service = ServiceAt(Utc(utcNow), FixedZone(offsetHours));

        await service.SnoozeFollowUpAsync(id, followUp, period);

        Assert.Equal(Date(expected), (await Details(id)).FollowUpOn);
    }

    [Theory]
    // Spring change in London (29 Mar 2026): 23:30 UTC is 00:30 BST on the 30th.
    [InlineData("Europe/London", "2026-03-29T23:30:00", "2026-03-31")]
    // Autumn change in London (25 Oct 2026): 23:30 UTC is 23:30 GMT, still the 25th.
    [InlineData("Europe/London", "2026-10-25T23:30:00", "2026-10-26")]
    public async Task Snoozing_on_a_dst_change_day_counts_from_the_local_date(string zoneId, string utcNow, string expected)
    {
        var followUp = new DateOnly(2026, 1, 1);
        var id = await Create(followUp);
        var service = ServiceAt(Utc(utcNow), TimeZoneInfo.FindSystemTimeZoneById(zoneId));

        await service.SnoozeFollowUpAsync(id, followUp, SnoozePeriod.Tomorrow);

        Assert.Equal(Date(expected), (await Details(id)).FollowUpOn);
    }
}
