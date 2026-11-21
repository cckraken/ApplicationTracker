using ApplicationTracker.Core.Applications;
using ApplicationTracker.Core.Domain;
using ApplicationTracker.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace ApplicationTracker.Tests.Applications;

public sealed class BoardTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 21);

    private readonly SqliteTestDatabase _db = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly ApplicationService _service;

    public BoardTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _service = new ApplicationService(_db, _time);
    }

    public void Dispose() => _db.Dispose();

    private Task<int> Create(
        string role, ApplicationStatus status = ApplicationStatus.Wishlist,
        DateOnly? followUpOn = null, string company = "Contoso",
        string? location = null, string? salary = null) =>
        _service.CreateAsync(new ApplicationInput
        {
            Company = company,
            RoleTitle = role,
            Status = status,
            FollowUpOn = followUpOn,
            Location = location,
            SalaryRange = salary,
        });

    private static IReadOnlyList<BoardCard> Column(BoardView board, ApplicationStatus status) =>
        board.Columns.Single(c => c.Status == status).Cards;

    [Fact]
    public async Task Empty_board_has_every_open_column_in_pipeline_order()
    {
        var board = await _service.GetBoardAsync(null);

        Assert.Equal(
            [ApplicationStatus.Wishlist, ApplicationStatus.Applied, ApplicationStatus.Screening,
             ApplicationStatus.Interview, ApplicationStatus.Offer],
            board.Columns.Select(c => c.Status));
        Assert.All(board.Columns, c => Assert.Empty(c.Cards));
        Assert.Empty(board.Closed);
    }

    [Fact]
    public async Task Cards_are_grouped_by_status_and_closed_ones_go_together()
    {
        await Create("Wish");
        await Create("Interviewing", ApplicationStatus.Interview);
        await Create("Turned down", ApplicationStatus.Rejected);
        await Create("Gave up", ApplicationStatus.Withdrawn);

        var board = await _service.GetBoardAsync(null);

        Assert.Equal(["Wish"], Column(board, ApplicationStatus.Wishlist).Select(c => c.RoleTitle));
        Assert.Equal(["Interviewing"], Column(board, ApplicationStatus.Interview).Select(c => c.RoleTitle));
        Assert.Empty(Column(board, ApplicationStatus.Applied));
        Assert.Equal(["Gave up", "Turned down"], board.Closed.Select(c => c.RoleTitle).Order());
    }

    [Fact]
    public async Task Card_carries_the_application_fields()
    {
        var id = await Create("Analyst", ApplicationStatus.Applied, Today.AddDays(3),
            company: "Fabrikam", location: "Leeds", salary: "£40k");

        var card = Assert.Single(Column(await _service.GetBoardAsync(null), ApplicationStatus.Applied));

        Assert.Equal(
            new BoardCard(id, "Fabrikam", "Analyst", ApplicationStatus.Applied, "Leeds", "£40k",
                Today, Today.AddDays(3), FollowUpState.Upcoming, 0),
            card);
    }

    [Fact]
    public async Task Open_cards_are_ordered_by_follow_up_urgency()
    {
        await Create("No follow-up");
        await Create("Upcoming later", followUpOn: Today.AddDays(5));
        await Create("Upcoming soon", followUpOn: Today.AddDays(1));
        await Create("Due today", followUpOn: Today);
        await Create("Overdue recently", followUpOn: Today.AddDays(-1));
        await Create("Overdue longest", followUpOn: Today.AddDays(-4));

        var cards = Column(await _service.GetBoardAsync(null), ApplicationStatus.Wishlist);

        Assert.Equal(
            ["Overdue longest", "Overdue recently", "Due today", "Upcoming soon", "Upcoming later", "No follow-up"],
            cards.Select(c => c.RoleTitle));
    }

    [Fact]
    public async Task Without_follow_up_the_card_waiting_longest_comes_first()
    {
        await Create("Older");
        _time.Advance(TimeSpan.FromDays(2));
        await Create("Newer");

        var cards = Column(await _service.GetBoardAsync(null), ApplicationStatus.Wishlist);

        Assert.Equal(["Older", "Newer"], cards.Select(c => c.RoleTitle));
    }

    [Fact]
    public async Task Closed_cards_show_the_most_recently_closed_first()
    {
        var first = await Create("Closed first");
        var second = await Create("Closed second");
        await _service.ChangeStatusAsync(first, ApplicationStatus.Rejected);
        _time.Advance(TimeSpan.FromDays(3));
        await _service.ChangeStatusAsync(second, ApplicationStatus.Withdrawn);

        var board = await _service.GetBoardAsync(null);

        Assert.Equal(["Closed second", "Closed first"], board.Closed.Select(c => c.RoleTitle));
    }

    [Fact]
    public async Task Closed_cards_have_no_follow_up_state()
    {
        await Create("Rejected", ApplicationStatus.Rejected, Today.AddDays(-2));

        var card = Assert.Single((await _service.GetBoardAsync(null)).Closed);

        Assert.Equal(FollowUpState.None, card.FollowUpState);
    }

    [Fact]
    public async Task Days_in_stage_counts_from_creation_when_there_is_no_history()
    {
        await Create("Wish");
        _time.Advance(TimeSpan.FromDays(4));

        var card = Assert.Single(Column(await _service.GetBoardAsync(null), ApplicationStatus.Wishlist));

        Assert.Equal(4, card.DaysInStage);
    }

    [Fact]
    public async Task Days_in_stage_counts_from_the_latest_status_change()
    {
        var id = await Create("Moving", ApplicationStatus.Applied);
        _time.Advance(TimeSpan.FromDays(10));
        await _service.ChangeStatusAsync(id, ApplicationStatus.Screening);
        _time.Advance(TimeSpan.FromDays(2));

        var card = Assert.Single(Column(await _service.GetBoardAsync(null), ApplicationStatus.Screening));

        Assert.Equal(2, card.DaysInStage);
    }

    [Fact]
    public async Task Days_in_stage_uses_the_local_calendar_date()
    {
        // UTC+2: 23:30 UTC on the 20th is already 01:30 on the 21st locally.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 23, 30, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("UTC+2", TimeSpan.FromHours(2), "UTC+2", "UTC+2"));
        var service = new ApplicationService(_db, time);
        await service.CreateAsync(new ApplicationInput { Company = "Contoso", RoleTitle = "Late night" });
        time.SetUtcNow(new DateTimeOffset(2026, 11, 21, 20, 0, 0, TimeSpan.Zero));

        var card = Assert.Single(Column(await service.GetBoardAsync(null), ApplicationStatus.Wishlist));

        Assert.Equal(0, card.DaysInStage);
    }

    [Fact]
    public void Days_in_stage_is_never_negative()
    {
        Assert.Equal(0, _service.GetDaysInStage(Start.UtcDateTime.AddDays(3), Today));
    }

    [Fact]
    public void Days_in_stage_rejects_a_local_timestamp()
    {
        Assert.Throws<ArgumentException>(() => _service.GetDaysInStage(new DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Local), Today));
    }

    [Fact]
    public async Task Search_matches_company_or_role_case_insensitively()
    {
        await Create("Analyst", company: "Société Générale");
        await Create("Engineer", company: "Contoso");
        await Create("Data Analyst", ApplicationStatus.Rejected, company: "Fabrikam");

        var board = await _service.GetBoardAsync("  SOCIÉTÉ ");
        Assert.Equal(["Analyst"], board.Columns.SelectMany(c => c.Cards).Select(c => c.RoleTitle));
        Assert.Empty(board.Closed);

        board = await _service.GetBoardAsync("analyst");
        Assert.Equal(["Analyst"], board.Columns.SelectMany(c => c.Cards).Select(c => c.RoleTitle));
        Assert.Equal(["Data Analyst"], board.Closed.Select(c => c.RoleTitle));
    }

    [Theory]
    [InlineData(ApplicationStatus.Wishlist, null, ApplicationStatus.Applied)]
    [InlineData(ApplicationStatus.Applied, ApplicationStatus.Wishlist, ApplicationStatus.Screening)]
    [InlineData(ApplicationStatus.Screening, ApplicationStatus.Applied, ApplicationStatus.Interview)]
    [InlineData(ApplicationStatus.Interview, ApplicationStatus.Screening, ApplicationStatus.Offer)]
    [InlineData(ApplicationStatus.Offer, ApplicationStatus.Interview, null)]
    [InlineData(ApplicationStatus.Rejected, null, null)]
    [InlineData(ApplicationStatus.Withdrawn, null, null)]
    public void Pipeline_neighbours(ApplicationStatus status, ApplicationStatus? previous, ApplicationStatus? next)
    {
        Assert.Equal(previous, status.PreviousInPipeline());
        Assert.Equal(next, status.NextInPipeline());
    }

    // ---- Pipeline definition ----

    [Fact]
    public void Pipeline_EveryStatus_IsEitherInThePipelineOrClosed()
    {
        Assert.All(Enum.GetValues<ApplicationStatus>(),
            s => Assert.NotEqual(s.IsClosed(), ApplicationStatusExtensions.Pipeline.Contains(s)));
    }

    // ---- Days in stage: history edge cases ----

    [Fact]
    public async Task GetBoardAsync_MovedOutAndBackIntoStage_CountsFromTheReturn()
    {
        var id = await Create("Boomerang", ApplicationStatus.Applied);
        _time.Advance(TimeSpan.FromDays(10));
        await _service.ChangeStatusAsync(id, ApplicationStatus.Screening);
        _time.Advance(TimeSpan.FromDays(3));
        await _service.ChangeStatusAsync(id, ApplicationStatus.Applied);
        _time.Advance(TimeSpan.FromDays(1));

        var card = Assert.Single(Column(await _service.GetBoardAsync(null), ApplicationStatus.Applied));

        Assert.Equal(1, card.DaysInStage);
    }

    [Fact]
    public async Task GetBoardAsync_LatestHistoryRowNotLastInserted_UsesLatestChangedAt()
    {
        var id = await Create("Backfilled", ApplicationStatus.Applied); // history row at Start
        using (var db = _db.CreateDbContext())
        {
            // Inserted afterwards (higher Id) but dated earlier, e.g. a backfilled history entry.
            db.StatusChanges.Add(new StatusChange
            {
                JobApplicationId = id,
                FromStatus = ApplicationStatus.Wishlist,
                ToStatus = ApplicationStatus.Applied,
                ChangedAt = Start.UtcDateTime.AddDays(-20),
            });
            await db.SaveChangesAsync();
        }
        _time.Advance(TimeSpan.FromDays(3));

        var card = Assert.Single(Column(await _service.GetBoardAsync(null), ApplicationStatus.Applied));

        Assert.Equal(3, card.DaysInStage);
    }

    [Fact]
    public async Task GetBoardAsync_EditWithoutStatusChange_DoesNotResetDaysInStage()
    {
        var id = await Create("Edited", ApplicationStatus.Applied);
        _time.Advance(TimeSpan.FromDays(5));
        await _service.UpdateAsync(id, new ApplicationInput
        {
            Company = "Contoso",
            RoleTitle = "Edited",
            Status = ApplicationStatus.Applied,
            OriginalStatus = ApplicationStatus.Applied,
            Location = "Remote",
        });

        var card = Assert.Single(Column(await _service.GetBoardAsync(null), ApplicationStatus.Applied));

        Assert.Equal(5, card.DaysInStage);
    }

    [Fact]
    public async Task GetBoardAsync_ChangeToSameStatus_DoesNotResetDaysInStage()
    {
        var id = await Create("Same", ApplicationStatus.Screening);
        _time.Advance(TimeSpan.FromDays(4));
        await _service.ChangeStatusAsync(id, ApplicationStatus.Screening);

        var card = Assert.Single(Column(await _service.GetBoardAsync(null), ApplicationStatus.Screening));

        Assert.Equal(4, card.DaysInStage);
    }

    // ---- Days in stage: time zones and DST ----

    private static TimeZoneInfo NewYork => TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private ApplicationService ServiceIn(TimeZoneInfo zone)
    {
        var time = new FakeTimeProvider(Start);
        time.SetLocalTimeZone(zone);
        return new ApplicationService(_db, time);
    }

    [Theory]
    // BST (UTC+1): 23:30 UTC on 1 July is 00:30 on 2 July locally.
    [InlineData("Europe/London", "2026-07-01T23:30:00", "2026-07-02", 0)]
    // GMT (UTC+0) in winter: 23:30 UTC on 1 Jan is still 1 Jan locally.
    [InlineData("Europe/London", "2027-01-01T23:30:00", "2027-01-02", 1)]
    // Across the autumn change (25 October 2026, a 25-hour day): 23:30 BST on the 24th.
    [InlineData("Europe/London", "2026-10-24T22:30:00", "2026-10-26", 2)]
    // Across the spring change (29 March 2026, a 23-hour day): 23:30 GMT on the 28th.
    [InlineData("Europe/London", "2026-03-28T23:30:00", "2026-03-30", 2)]
    // UTC-5: 03:00 UTC on the 21st is 22:00 on the 20th locally.
    [InlineData("America/New_York", "2026-11-21T03:00:00", "2026-11-21", 1)]
    // 04:30 UTC is 00:30 EDT (UTC-4) in summer but 23:30 EST (UTC-5) the previous day in winter.
    [InlineData("America/New_York", "2026-07-02T04:30:00", "2026-07-02", 0)]
    [InlineData("America/New_York", "2026-12-02T04:30:00", "2026-12-02", 1)]
    public void GetDaysInStage_LocalZoneWithDst_CountsLocalCalendarDays(
        string zoneId, string startedUtc, string today, int expected)
    {
        var service = ServiceIn(TimeZoneInfo.FindSystemTimeZoneById(zoneId));
        var started = DateTime.SpecifyKind(
            DateTime.Parse(startedUtc, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

        Assert.Equal(expected, service.GetDaysInStage(
            started, DateOnly.Parse(today, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void GetDaysInStage_UnspecifiedKind_IsTreatedAsUtc()
    {
        // Values without a Kind are UTC by convention (timestamps are stored in UTC).
        var service = ServiceIn(NewYork);
        var unspecified = new DateTime(2026, 11, 21, 3, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(1, service.GetDaysInStage(unspecified, new DateOnly(2026, 11, 21)));
    }

    [Fact]
    public async Task GetBoardAsync_LateEveningInNegativeOffsetZone_UsesLocalTodayNotUtcDate()
    {
        // New York: created 15:00 local on the 20th, viewed 22:00 local the same day (03:00 UTC on the 21st).
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 20, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(NewYork);
        var service = new ApplicationService(_db, time);
        await service.CreateAsync(new ApplicationInput
        {
            Company = "Contoso",
            RoleTitle = "Evening",
            Status = ApplicationStatus.Applied,
            FollowUpOn = new DateOnly(2026, 11, 20),
        });
        time.SetUtcNow(new DateTimeOffset(2026, 11, 21, 3, 0, 0, TimeSpan.Zero));

        var card = Assert.Single(Column(await service.GetBoardAsync(null), ApplicationStatus.Applied));

        Assert.Equal(0, card.DaysInStage);
        Assert.Equal(FollowUpState.DueToday, card.FollowUpState);
        Assert.Equal(new DateOnly(2026, 11, 20), card.AppliedOn);
    }

    // ---- Ordering tie-breaks ----

    [Fact]
    public async Task GetBoardAsync_SameFollowUpDate_LongerInStageComesFirstEvenWithHigherId()
    {
        var returned = await Create("Returned", followUpOn: Today);
        await Create("Waiting", followUpOn: Today);
        _time.Advance(TimeSpan.FromDays(2));
        await _service.ChangeStatusAsync(returned, ApplicationStatus.Applied);
        await _service.ChangeStatusAsync(returned, ApplicationStatus.Wishlist);

        var cards = Column(await _service.GetBoardAsync(null), ApplicationStatus.Wishlist);

        Assert.Equal(["Waiting", "Returned"], cards.Select(c => c.RoleTitle));
        Assert.All(cards, c => Assert.Equal(FollowUpState.Overdue, c.FollowUpState));
    }

    [Fact]
    public async Task GetBoardAsync_OverdueCard_OutranksUpcomingCardLongerInStage()
    {
        await Create("Long wait, upcoming", followUpOn: Today.AddDays(30));
        _time.Advance(TimeSpan.FromDays(20));
        await Create("Fresh, overdue", followUpOn: Today.AddDays(20));
        _time.Advance(TimeSpan.FromDays(1));

        var cards = Column(await _service.GetBoardAsync(null), ApplicationStatus.Wishlist);

        Assert.Equal(["Fresh, overdue", "Long wait, upcoming"], cards.Select(c => c.RoleTitle));
    }

    [Fact]
    public async Task GetBoardAsync_ClosedOnSameDay_MostRecentlyClosedFirst()
    {
        // BoardView documents "most recently closed first"; here both close on the same day, hours apart.
        var first = await Create("Closed at 09:30");
        var second = await Create("Closed at 15:30");
        await _service.ChangeStatusAsync(first, ApplicationStatus.Rejected);
        _time.Advance(TimeSpan.FromHours(6));
        await _service.ChangeStatusAsync(second, ApplicationStatus.Rejected);

        var board = await _service.GetBoardAsync(null);

        Assert.Equal(["Closed at 15:30", "Closed at 09:30"], board.Closed.Select(c => c.RoleTitle));
    }

    // ---- Search ----

    [Theory]
    [InlineData("100%", new[] { "100% remote" })]
    [InlineData("%", new[] { "100% remote" })]
    [InlineData("a_b", new[] { "a_b tester" })]
    [InlineData("   ", new[] { "100% remote", "1000 remote", "a_b tester", "axb tester" })]
    public async Task GetBoardAsync_SearchWithLikeSpecialCharacters_MatchesLiterally(string search, string[] expected)
    {
        await Create("100% remote");
        await Create("1000 remote");
        await Create("a_b tester");
        await Create("axb tester");

        var board = await _service.GetBoardAsync(search);

        Assert.Equal(expected, board.Columns.SelectMany(c => c.Cards).Select(c => c.RoleTitle).Order());
    }

    [Fact]
    public async Task ListAsync_SearchWithPercent_MatchesLiterally()
    {
        await Create("100% remote");
        await Create("1000 remote");

        var result = await _service.ListAsync(new ApplicationFilter { Search = "0%" });

        Assert.Equal(["100% remote"], result.Select(r => r.RoleTitle));
    }
}
