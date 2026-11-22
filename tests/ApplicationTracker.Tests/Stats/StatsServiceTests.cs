using System.Globalization;
using ApplicationTracker.Core.Applications;
using ApplicationTracker.Core.Data;
using ApplicationTracker.Core.Domain;
using ApplicationTracker.Core.Stats;
using ApplicationTracker.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace ApplicationTracker.Tests.Stats;

public sealed class StatsServiceTests : IDisposable
{
    // Saturday 21 November 2026.
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 21);
    private static readonly DateOnly CurrentWeek = new(2026, 11, 16);
    private static readonly DateOnly OldestWeek = new(2026, 8, 31);

    private readonly SqliteTestDatabase _db = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly StatsService _stats;
    private readonly ApplicationService _applications;

    public StatsServiceTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _stats = new StatsService(_db, _time);
        _applications = new ApplicationService(_db, _time);
    }

    public void Dispose() => _db.Dispose();

    // ---- Helpers ----

    private Task<int> Create(
        ApplicationStatus status = ApplicationStatus.Wishlist, DateOnly? appliedOn = null, string role = "Data Analyst") =>
        _applications.CreateAsync(new ApplicationInput
        {
            Company = "Contoso",
            RoleTitle = role,
            Status = status,
            AppliedOn = appliedOn,
        });

    private async Task Move(int id, params ApplicationStatus[] path)
    {
        foreach (var status in path)
        {
            await _applications.MoveToAsync(id, status);
        }
    }

    private void AdvanceDays(double days) => _time.Advance(TimeSpan.FromDays(days));

    private Task<StatsDashboard> Dashboard() => _stats.GetDashboardAsync();

    /// <summary>Services sharing one clock that starts at <paramref name="utcNow"/> in <paramref name="zone"/>.</summary>
    private (ApplicationService Applications, StatsService Stats, FakeTimeProvider Time) ServiceIn(
        DateTimeOffset utcNow, TimeZoneInfo zone)
    {
        var time = new FakeTimeProvider(utcNow);
        time.SetLocalTimeZone(zone);
        return (new ApplicationService(_db, time), new StatsService(_db, time), time);
    }

    private static TimeZoneInfo Zone(string ianaId) => TimeZoneInfo.FindSystemTimeZoneById(ianaId);

    private static DateOnly Date(string value) => DateOnly.Parse(value, CultureInfo.InvariantCulture);

    private static int CountFor(StatsDashboard dashboard, ApplicationStatus status) =>
        dashboard.ByStatus.Single(c => c.Status == status).Count;

    private static int WeekCountFor(StatsDashboard dashboard, DateOnly weekStart) =>
        dashboard.PerWeek.Single(w => w.WeekStart == weekStart).Count;

    private static StageDuration Stage(StatsDashboard dashboard, ApplicationStatus status) =>
        dashboard.TimeInStage.Single(s => s.Status == status);

    // ---- Empty database ----

    [Fact]
    public async Task Empty_database_gives_zeros_everywhere_and_no_averages()
    {
        var dashboard = await Dashboard();

        Assert.Equal(0, dashboard.TotalApplications);
        Assert.All(dashboard.ByStatus, c => Assert.Equal(0, c.Count));
        Assert.Equal(new ResponseRate(0, 0, null), dashboard.Response);
        Assert.Equal(StatsService.WeeksShown, dashboard.PerWeek.Count);
        Assert.All(dashboard.PerWeek, w => Assert.Equal(0, w.Count));
        Assert.All(dashboard.TimeInStage, s => Assert.Equal((null, 0), (s.AverageDays, s.Stays)));
    }

    // ---- Counts by status ----

    [Fact]
    public async Task Counts_by_status_list_every_status_in_pipeline_order_then_closed()
    {
        var dashboard = await Dashboard();

        Assert.Equal(
            [ApplicationStatus.Wishlist, ApplicationStatus.Applied, ApplicationStatus.Screening,
             ApplicationStatus.Interview, ApplicationStatus.Offer, ApplicationStatus.Rejected, ApplicationStatus.Withdrawn],
            dashboard.ByStatus.Select(c => c.Status));
    }

    [Fact]
    public async Task Counts_by_status_use_the_current_status_and_total_includes_closed()
    {
        await Create(ApplicationStatus.Wishlist);
        await Create(ApplicationStatus.Applied);
        await Create(ApplicationStatus.Applied);
        var moved = await Create(ApplicationStatus.Applied);
        await Move(moved, ApplicationStatus.Screening, ApplicationStatus.Interview);
        await Create(ApplicationStatus.Rejected);
        await Create(ApplicationStatus.Withdrawn);

        var dashboard = await Dashboard();

        Assert.Equal(6, dashboard.TotalApplications);
        Assert.Equal([1, 2, 0, 1, 0, 1, 1], dashboard.ByStatus.Select(c => c.Count));
    }

    // ---- Response rate ----

    [Fact]
    public async Task Response_rate_is_null_when_only_wishlist_applications_exist()
    {
        await Create(ApplicationStatus.Wishlist);

        Assert.Equal(new ResponseRate(0, 0, null), (await Dashboard()).Response);
    }

    [Fact]
    public async Task Rejection_straight_from_applied_is_not_a_response()
    {
        var id = await Create(ApplicationStatus.Applied);
        await Move(id, ApplicationStatus.Rejected);

        Assert.Equal(new ResponseRate(0, 1, 0.0), (await Dashboard()).Response);
    }

    [Fact]
    public async Task Rejection_after_screening_is_a_response()
    {
        var id = await Create(ApplicationStatus.Applied);
        await Move(id, ApplicationStatus.Screening, ApplicationStatus.Rejected);

        Assert.Equal(new ResponseRate(1, 1, 1.0), (await Dashboard()).Response);
    }

    [Theory]
    [InlineData(ApplicationStatus.Screening)]
    [InlineData(ApplicationStatus.Interview)]
    [InlineData(ApplicationStatus.Offer)]
    public async Task Reaching_a_response_stage_counts_as_a_response(ApplicationStatus reached)
    {
        var id = await Create(ApplicationStatus.Applied);
        await Move(id, reached);

        Assert.Equal(new ResponseRate(1, 1, 1.0), (await Dashboard()).Response);
    }

    [Fact]
    public async Task Response_rate_is_responded_over_applied()
    {
        var screened = await Create(ApplicationStatus.Applied);
        await Move(screened, ApplicationStatus.Screening);
        var rejected = await Create(ApplicationStatus.Applied);
        await Move(rejected, ApplicationStatus.Rejected);
        await Create(ApplicationStatus.Applied);
        await Create(ApplicationStatus.Applied);
        await Create(ApplicationStatus.Wishlist);

        Assert.Equal(new ResponseRate(1, 4, 0.25), (await Dashboard()).Response);
    }

    [Fact]
    public async Task Response_counts_even_after_moving_back_to_applied()
    {
        var id = await Create(ApplicationStatus.Applied);
        await Move(id, ApplicationStatus.Screening, ApplicationStatus.Applied);

        Assert.Equal(new ResponseRate(1, 1, 1.0), (await Dashboard()).Response);
    }

    [Fact]
    public async Task Created_directly_in_interview_counts_as_applied_and_responded()
    {
        await Create(ApplicationStatus.Interview);

        Assert.Equal(new ResponseRate(1, 1, 1.0), (await Dashboard()).Response);
    }

    [Fact]
    public async Task Moved_back_to_wishlist_still_counts_as_applied()
    {
        var id = await Create(ApplicationStatus.Wishlist);
        await Move(id, ApplicationStatus.Applied, ApplicationStatus.Wishlist);

        Assert.Equal(new ResponseRate(0, 1, 0.0), (await Dashboard()).Response);
    }

    [Fact]
    public async Task Wishlist_application_with_an_applied_on_date_counts_as_applied()
    {
        var id = await Create(ApplicationStatus.Wishlist);
        await _applications.UpdateAsync(id, new ApplicationInput
        {
            Company = "Contoso",
            RoleTitle = "Data Analyst",
            Status = ApplicationStatus.Wishlist,
            OriginalStatus = ApplicationStatus.Wishlist,
            AppliedOn = Today.AddDays(-3),
        });

        Assert.Equal(new ResponseRate(0, 1, 0.0), (await Dashboard()).Response);
    }

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task Closing_straight_from_wishlist_does_not_count_as_applied(ApplicationStatus closed)
    {
        // As documented on ResponseRate: closing straight from Wishlist is not applying.
        var id = await Create(ApplicationStatus.Wishlist);
        await Move(id, closed);

        Assert.Equal(new ResponseRate(0, 0, null), (await Dashboard()).Response);
    }

    // ---- Applications per week ----

    [Fact]
    public async Task Per_week_covers_twelve_monday_weeks_oldest_first_ending_with_the_current_week()
    {
        var weeks = (await Dashboard()).PerWeek;

        Assert.Equal(12, weeks.Count);
        Assert.Equal(OldestWeek, weeks[0].WeekStart);
        Assert.Equal(CurrentWeek, weeks[^1].WeekStart);
        Assert.All(weeks, w => Assert.Equal(DayOfWeek.Monday, w.WeekStart.DayOfWeek));
        Assert.Equal(weeks.Select(w => w.WeekStart).Order(), weeks.Select(w => w.WeekStart));
    }

    [Fact]
    public async Task Monday_and_sunday_fall_in_the_same_week()
    {
        await Create(ApplicationStatus.Applied, CurrentWeek);              // Monday
        await Create(ApplicationStatus.Applied, CurrentWeek.AddDays(6));   // Sunday
        await Create(ApplicationStatus.Applied, CurrentWeek.AddDays(-1));  // previous Sunday

        var dashboard = await Dashboard();

        Assert.Equal(2, WeekCountFor(dashboard, CurrentWeek));
        Assert.Equal(1, WeekCountFor(dashboard, CurrentWeek.AddDays(-7)));
    }

    [Fact]
    public async Task Dates_outside_the_twelve_weeks_are_ignored()
    {
        await Create(ApplicationStatus.Applied, OldestWeek);                // first day of the window
        await Create(ApplicationStatus.Applied, OldestWeek.AddDays(-1));    // Sunday before it
        await Create(ApplicationStatus.Applied, CurrentWeek.AddDays(7));    // next Monday

        var dashboard = await Dashboard();

        Assert.Equal(1, WeekCountFor(dashboard, OldestWeek));
        Assert.Equal(1, dashboard.PerWeek.Sum(w => w.Count));
    }

    [Fact]
    public async Task Applications_without_an_applied_on_date_are_not_counted_per_week()
    {
        await Create(ApplicationStatus.Wishlist);

        Assert.Equal(0, (await Dashboard()).PerWeek.Sum(w => w.Count));
    }

    [Fact]
    public async Task Applications_that_skip_applied_are_counted_this_week_and_in_the_response_rate()
    {
        await Create(ApplicationStatus.Screening);
        var moved = await Create(ApplicationStatus.Wishlist);
        await Move(moved, ApplicationStatus.Interview);

        var dashboard = await Dashboard();

        Assert.Equal(2, WeekCountFor(dashboard, CurrentWeek));
        Assert.Equal(new ResponseRate(2, 2, 1.0), dashboard.Response);
    }

    [Fact]
    public async Task Per_week_counts_by_applied_on_not_creation_date()
    {
        await Create(ApplicationStatus.Applied, Today.AddDays(-21));

        var dashboard = await Dashboard();

        Assert.Equal(1, WeekCountFor(dashboard, CurrentWeek.AddDays(-21)));
        Assert.Equal(0, WeekCountFor(dashboard, CurrentWeek));
    }

    [Fact]
    public async Task On_a_monday_the_current_week_starts_today()
    {
        var (applications, stats, _) = ServiceIn(new DateTimeOffset(2026, 11, 23, 8, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc);
        await applications.CreateAsync(new ApplicationInput
        {
            Company = "Contoso",
            RoleTitle = "Monday",
            Status = ApplicationStatus.Applied,
        });

        var weeks = (await stats.GetDashboardAsync()).PerWeek;

        Assert.Equal(Date("2026-11-23"), weeks[^1].WeekStart);
        Assert.Equal(1, weeks[^1].Count);
    }

    [Fact]
    public async Task Late_sunday_evening_uses_the_local_week_not_the_utc_one()
    {
        // New York: 22:00 EST on Sunday 22 November is 03:00 UTC on Monday 23 November.
        var (applications, stats, _) = ServiceIn(
            new DateTimeOffset(2026, 11, 23, 3, 0, 0, TimeSpan.Zero), Zone("America/New_York"));
        await applications.CreateAsync(new ApplicationInput
        {
            Company = "Contoso",
            RoleTitle = "Late evening",
            Status = ApplicationStatus.Applied,
        });

        var weeks = (await stats.GetDashboardAsync()).PerWeek;

        Assert.Equal(CurrentWeek, weeks[^1].WeekStart);
        Assert.Equal(1, weeks[^1].Count);
    }

    [Fact]
    public async Task Early_monday_morning_after_the_spring_clock_change_starts_the_new_week()
    {
        // London: 00:30 BST on Monday 30 March 2026 (the day after the change) is 23:30 UTC on Sunday 29 March.
        var (applications, stats, _) = ServiceIn(
            new DateTimeOffset(2026, 3, 29, 23, 30, 0, TimeSpan.Zero), Zone("Europe/London"));
        await applications.CreateAsync(new ApplicationInput
        {
            Company = "Contoso",
            RoleTitle = "Early morning",
            Status = ApplicationStatus.Applied,
        });

        var weeks = (await stats.GetDashboardAsync()).PerWeek;

        Assert.Equal(Date("2026-03-30"), weeks[^1].WeekStart);
        Assert.Equal(1, weeks[^1].Count);
        Assert.Equal(Date("2026-03-23"), weeks[^2].WeekStart);
    }

    [Theory]
    [InlineData("2026-11-16", "2026-11-16")] // Monday
    [InlineData("2026-11-21", "2026-11-16")] // Saturday
    [InlineData("2026-11-22", "2026-11-16")] // Sunday
    [InlineData("2026-03-29", "2026-03-23")] // Sunday of the spring clock change
    [InlineData("2026-01-03", "2025-12-29")] // across a year end
    public void Week_start_is_the_monday_on_or_before_the_date(string date, string monday)
    {
        Assert.Equal(Date(monday), StatsService.WeekStart(Date(date)));
    }

    // ---- Time in stage ----

    [Fact]
    public async Task Time_in_stage_lists_the_open_stages_in_pipeline_order()
    {
        var stages = (await Dashboard()).TimeInStage;

        Assert.Equal(ApplicationStatusExtensions.Pipeline, stages.Select(s => s.Status));
    }

    [Fact]
    public async Task Each_finished_stay_runs_from_entering_the_stage_to_the_next_change()
    {
        var id = await Create(ApplicationStatus.Wishlist);  // Wishlist from creation
        AdvanceDays(2);
        await Move(id, ApplicationStatus.Applied);
        AdvanceDays(3);
        await Move(id, ApplicationStatus.Screening);
        AdvanceDays(1);
        await Move(id, ApplicationStatus.Rejected);
        AdvanceDays(10);

        var dashboard = await Dashboard();

        Assert.Equal(new StageDuration(ApplicationStatus.Wishlist, 2, 1), Stage(dashboard, ApplicationStatus.Wishlist));
        Assert.Equal(new StageDuration(ApplicationStatus.Applied, 3, 1), Stage(dashboard, ApplicationStatus.Applied));
        Assert.Equal(new StageDuration(ApplicationStatus.Screening, 1, 1), Stage(dashboard, ApplicationStatus.Screening));
        Assert.Equal(new StageDuration(ApplicationStatus.Interview, null, 0), Stage(dashboard, ApplicationStatus.Interview));
    }

    [Fact]
    public async Task The_current_unfinished_stay_is_not_counted()
    {
        await Create(ApplicationStatus.Wishlist);
        var id = await Create(ApplicationStatus.Applied);
        AdvanceDays(4);
        await Move(id, ApplicationStatus.Screening);
        AdvanceDays(30);

        var dashboard = await Dashboard();

        Assert.Equal(new StageDuration(ApplicationStatus.Wishlist, null, 0), Stage(dashboard, ApplicationStatus.Wishlist));
        Assert.Equal(new StageDuration(ApplicationStatus.Applied, 4, 1), Stage(dashboard, ApplicationStatus.Applied));
        Assert.Equal(new StageDuration(ApplicationStatus.Screening, null, 0), Stage(dashboard, ApplicationStatus.Screening));
    }

    [Fact]
    public async Task Created_directly_as_applied_has_no_zero_length_wishlist_stay()
    {
        var id = await Create(ApplicationStatus.Applied);
        AdvanceDays(1);
        await Move(id, ApplicationStatus.Screening);

        var dashboard = await Dashboard();

        Assert.Equal(new StageDuration(ApplicationStatus.Wishlist, null, 0), Stage(dashboard, ApplicationStatus.Wishlist));
        Assert.Equal(new StageDuration(ApplicationStatus.Applied, 1, 1), Stage(dashboard, ApplicationStatus.Applied));
    }

    [Fact]
    public async Task Repeat_visits_to_a_stage_count_as_separate_stays()
    {
        var id = await Create(ApplicationStatus.Applied);
        AdvanceDays(4);
        await Move(id, ApplicationStatus.Screening);
        AdvanceDays(2);
        await Move(id, ApplicationStatus.Applied);
        AdvanceDays(6);
        await Move(id, ApplicationStatus.Interview);

        var dashboard = await Dashboard();

        Assert.Equal(new StageDuration(ApplicationStatus.Applied, 5, 2), Stage(dashboard, ApplicationStatus.Applied));
        Assert.Equal(new StageDuration(ApplicationStatus.Screening, 2, 1), Stage(dashboard, ApplicationStatus.Screening));
    }

    [Fact]
    public async Task Time_in_stage_averages_across_applications_in_fractional_days()
    {
        var quick = await Create(ApplicationStatus.Applied, role: "Quick");
        var slow = await Create(ApplicationStatus.Applied, role: "Slow");
        AdvanceDays(0.5);
        await Move(quick, ApplicationStatus.Screening);
        AdvanceDays(1);
        await Move(slow, ApplicationStatus.Screening);

        var applied = Stage(await Dashboard(), ApplicationStatus.Applied);

        Assert.Equal(2, applied.Stays);
        Assert.Equal(1.0, applied.AverageDays!.Value, precision: 6);
    }

    [Fact]
    public async Task Stays_in_closed_statuses_are_not_listed()
    {
        var id = await Create(ApplicationStatus.Applied);
        await Move(id, ApplicationStatus.Rejected);
        AdvanceDays(3);
        await Move(id, ApplicationStatus.Applied);

        var stages = (await Dashboard()).TimeInStage;

        Assert.DoesNotContain(stages, s => s.Status.IsClosed());
        Assert.All(stages, s => Assert.Equal(0, s.Stays));
    }

    [Fact]
    public async Task Status_changed_through_the_edit_form_is_counted_like_a_move()
    {
        var id = await Create(ApplicationStatus.Applied);
        AdvanceDays(3);
        await _applications.UpdateAsync(id, new ApplicationInput
        {
            Company = "Contoso",
            RoleTitle = "Data Analyst",
            Status = ApplicationStatus.Interview,
            OriginalStatus = ApplicationStatus.Applied,
        });

        var dashboard = await Dashboard();

        Assert.Equal(new StageDuration(ApplicationStatus.Applied, 3, 1), Stage(dashboard, ApplicationStatus.Applied));
        Assert.Equal(new ResponseRate(1, 1, 1.0), dashboard.Response);
    }

    [Fact]
    public async Task A_stay_across_the_autumn_clock_change_is_measured_in_elapsed_time()
    {
        // London: 25 October 2026 is a 25-hour day. Midnight BST on the 24th to midnight GMT on the 26th is 49 hours.
        var (applications, stats, time) = ServiceIn(
            new DateTimeOffset(2026, 10, 23, 23, 0, 0, TimeSpan.Zero), Zone("Europe/London"));
        var id = await applications.CreateAsync(new ApplicationInput
        {
            Company = "Contoso",
            RoleTitle = "Autumn",
            Status = ApplicationStatus.Applied,
        });
        time.SetUtcNow(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero));
        await applications.MoveToAsync(id, ApplicationStatus.Screening);

        var applied = (await stats.GetDashboardAsync()).TimeInStage.Single(s => s.Status == ApplicationStatus.Applied);

        Assert.Equal(1, applied.Stays);
        Assert.Equal(49.0 / 24, applied.AverageDays!.Value, precision: 6);
    }

    // ---- Demo data ----

    // The exact figures below mirror DemoDataSeeder.Demos and PathTo: update them together when the demo data changes.
    [Fact]
    public async Task Demo_data_yields_a_full_dashboard()
    {
        using (var db = _db.CreateDbContext())
        {
            await DemoDataSeeder.SeedIfEmptyAsync(db, _time);
        }

        var dashboard = await Dashboard();

        Assert.Equal(12, dashboard.TotalApplications);
        Assert.Equal([2, 3, 2, 2, 1, 1, 1], dashboard.ByStatus.Select(c => c.Count));
        Assert.Equal(6, dashboard.Response.Responded);
        Assert.Equal(10, dashboard.Response.Applied);
        Assert.Equal(0.6, dashboard.Response.Rate!.Value, precision: 6);
        Assert.Equal(10, dashboard.PerWeek.Sum(w => w.Count));
        Assert.Equal(10, Stage(dashboard, ApplicationStatus.Wishlist).Stays);
        Assert.Equal(1.0, Stage(dashboard, ApplicationStatus.Wishlist).AverageDays!.Value, precision: 6);
    }
}
