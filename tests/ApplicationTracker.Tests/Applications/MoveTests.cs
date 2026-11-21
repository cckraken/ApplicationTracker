using ApplicationTracker.Core.Applications;
using ApplicationTracker.Core.Domain;
using ApplicationTracker.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace ApplicationTracker.Tests.Applications;

public sealed class MoveTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 30, 0, TimeSpan.Zero);

    private readonly SqliteTestDatabase _db = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly ApplicationService _service;

    public MoveTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _service = new ApplicationService(_db, _time);
    }

    public void Dispose() => _db.Dispose();

    private Task<int> Create(ApplicationStatus status = ApplicationStatus.Wishlist) =>
        _service.CreateAsync(new ApplicationInput { Company = "Contoso", RoleTitle = "Analyst", Status = status });

    private async Task<int> HistoryCount(int id)
    {
        using var db = _db.CreateDbContext();
        return await db.StatusChanges.CountAsync(s => s.JobApplicationId == id);
    }

    [Theory]
    [InlineData(ApplicationStatus.Wishlist, PipelineDirection.Forward, ApplicationStatus.Applied)]
    [InlineData(ApplicationStatus.Interview, PipelineDirection.Forward, ApplicationStatus.Offer)]
    [InlineData(ApplicationStatus.Applied, PipelineDirection.Back, ApplicationStatus.Wishlist)]
    [InlineData(ApplicationStatus.Offer, PipelineDirection.Back, ApplicationStatus.Interview)]
    public async Task Move_in_pipeline_goes_one_stage(
        ApplicationStatus from, PipelineDirection direction, ApplicationStatus expected)
    {
        var id = await Create(from);

        var result = await _service.MoveInPipelineAsync(id, direction);

        Assert.Equal(new MoveResult(MoveOutcome.Moved, expected), result);
    }

    [Theory]
    [InlineData(ApplicationStatus.Wishlist, PipelineDirection.Back)]
    [InlineData(ApplicationStatus.Offer, PipelineDirection.Forward)]
    [InlineData(ApplicationStatus.Rejected, PipelineDirection.Forward)]
    [InlineData(ApplicationStatus.Withdrawn, PipelineDirection.Back)]
    public async Task Move_in_pipeline_at_an_end_or_when_closed_changes_nothing(
        ApplicationStatus from, PipelineDirection direction)
    {
        var id = await Create(from);
        var historyBefore = await HistoryCount(id);

        var result = await _service.MoveInPipelineAsync(id, direction);

        Assert.Equal(new MoveResult(MoveOutcome.Unchanged, from), result);
        Assert.Equal(historyBefore, await HistoryCount(id));
    }

    [Fact]
    public async Task Repeated_moves_each_start_from_the_stored_status()
    {
        // Run one after the other (the board's move lock does the same for a double click):
        // each move is worked out from the stored status, not from what a caller last saw.
        var id = await Create();

        await _service.MoveInPipelineAsync(id, PipelineDirection.Forward);
        var second = await _service.MoveInPipelineAsync(id, PipelineDirection.Forward);

        Assert.Equal(new MoveResult(MoveOutcome.Moved, ApplicationStatus.Screening), second);
        Assert.Equal(2, await HistoryCount(id));
    }

    [Fact]
    public async Task Move_in_pipeline_sees_a_change_made_elsewhere()
    {
        var id = await Create(ApplicationStatus.Applied);
        await _service.ChangeStatusAsync(id, ApplicationStatus.Interview); // e.g. from the list page

        var result = await _service.MoveInPipelineAsync(id, PipelineDirection.Forward);

        Assert.Equal(new MoveResult(MoveOutcome.Moved, ApplicationStatus.Offer), result);
    }

    [Fact]
    public async Task Move_in_pipeline_forward_from_wishlist_fills_the_applied_date()
    {
        var id = await Create();

        await _service.MoveInPipelineAsync(id, PipelineDirection.Forward);

        var saved = await _service.GetForEditAsync(id);
        Assert.Equal(new DateOnly(2026, 11, 21), saved!.AppliedOn);
    }

    [Fact]
    public async Task Move_to_the_current_status_is_unchanged_and_writes_nothing()
    {
        var id = await Create(ApplicationStatus.Screening);
        var historyBefore = await HistoryCount(id);
        _time.Advance(TimeSpan.FromHours(1));

        var result = await _service.MoveToAsync(id, ApplicationStatus.Screening);

        Assert.Equal(new MoveResult(MoveOutcome.Unchanged, ApplicationStatus.Screening), result);
        Assert.Equal(historyBefore, await HistoryCount(id));
        Assert.Equal(Start.UtcDateTime, await UpdatedAt(id));
    }

    [Fact]
    public async Task Move_in_pipeline_records_the_change_in_history()
    {
        var id = await Create(ApplicationStatus.Applied);
        _time.Advance(TimeSpan.FromHours(2));

        await _service.MoveInPipelineAsync(id, PipelineDirection.Forward);

        var latest = await LatestChange(id);
        Assert.Equal(ApplicationStatus.Applied, latest.FromStatus);
        Assert.Equal(ApplicationStatus.Screening, latest.ToStatus);
        Assert.Equal(Start.UtcDateTime.AddHours(2), latest.ChangedAt);
    }

    [Fact]
    public async Task Close_records_the_change_in_history()
    {
        var id = await Create(ApplicationStatus.Offer);
        _time.Advance(TimeSpan.FromHours(3));

        var result = await _service.CloseAsync(id);

        Assert.Equal(MoveResult.Moved(ApplicationStatus.Rejected), result);
        var latest = await LatestChange(id);
        Assert.Equal(ApplicationStatus.Offer, latest.FromStatus);
        Assert.Equal(ApplicationStatus.Rejected, latest.ToStatus);
        Assert.Equal(Start.UtcDateTime.AddHours(3), latest.ChangedAt);
    }

    [Fact]
    public async Task Withdrawing_an_open_application_by_choice_is_allowed()
    {
        var id = await Create(ApplicationStatus.Applied);

        var result = await _service.MoveToAsync(id, ApplicationStatus.Withdrawn);

        Assert.Equal(MoveResult.Moved(ApplicationStatus.Withdrawn), result);
    }

    [Fact]
    public async Task Move_in_pipeline_rejects_an_unknown_direction()
    {
        var id = await Create();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => _service.MoveInPipelineAsync(id, (PipelineDirection)5));
    }

    private async Task<DateTime> UpdatedAt(int id)
    {
        using var db = _db.CreateDbContext();
        return await db.Applications.Where(a => a.Id == id).Select(a => a.UpdatedAt).SingleAsync();
    }

    private async Task<StatusChange> LatestChange(int id)
    {
        using var db = _db.CreateDbContext();
        return await db.StatusChanges.Where(s => s.JobApplicationId == id)
            .OrderByDescending(s => s.ChangedAt).FirstAsync();
    }

    [Fact]
    public async Task Move_to_any_status_is_allowed()
    {
        var id = await Create(ApplicationStatus.Withdrawn);

        var result = await _service.MoveToAsync(id, ApplicationStatus.Wishlist);

        Assert.Equal(new MoveResult(MoveOutcome.Moved, ApplicationStatus.Wishlist), result);
    }

    [Theory]
    [InlineData(ApplicationStatus.Wishlist)]
    [InlineData(ApplicationStatus.Interview)]
    public async Task Close_rejects_an_open_application(ApplicationStatus from)
    {
        var id = await Create(from);

        var result = await _service.CloseAsync(id);

        Assert.Equal(new MoveResult(MoveOutcome.Moved, ApplicationStatus.Rejected), result);
    }

    [Theory]
    [InlineData(ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public async Task Close_keeps_an_already_closed_status(ApplicationStatus from)
    {
        var id = await Create(from);
        var historyBefore = await HistoryCount(id);

        var result = await _service.CloseAsync(id);

        Assert.Equal(new MoveResult(MoveOutcome.Unchanged, from), result);
        Assert.Equal(historyBefore, await HistoryCount(id));
    }

    [Fact]
    public async Task Moves_on_a_deleted_application_report_not_found()
    {
        var id = await Create();
        await _service.DeleteAsync(id);

        var notFound = new MoveResult(MoveOutcome.NotFound, null);
        Assert.Equal(notFound, await _service.MoveInPipelineAsync(id, PipelineDirection.Forward));
        Assert.Equal(notFound, await _service.MoveToAsync(id, ApplicationStatus.Offer));
        Assert.Equal(notFound, await _service.CloseAsync(id));
        Assert.False(await _service.ChangeStatusAsync(id, ApplicationStatus.Offer));
    }
}
