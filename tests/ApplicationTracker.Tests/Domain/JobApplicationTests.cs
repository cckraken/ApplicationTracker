using ApplicationTracker.Core.Domain;

namespace ApplicationTracker.Tests.Domain;

public class JobApplicationTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 21);

    private static JobApplication NewApplication() => new() { RoleTitle = "Data Analyst" };

    [Fact]
    public void New_application_starts_on_wishlist_with_no_history()
    {
        var app = NewApplication();

        Assert.Equal(ApplicationStatus.Wishlist, app.CurrentStatus);
        Assert.Empty(app.StatusHistory);
    }

    [Fact]
    public void ChangeStatus_NewStatus_RecordsHistoryAndUpdatesStatus()
    {
        var app = NewApplication();

        var changed = app.ChangeStatus(ApplicationStatus.Applied, Now, Today);

        Assert.True(changed);
        Assert.Equal(ApplicationStatus.Applied, app.CurrentStatus);
        Assert.Equal(Now, app.UpdatedAt);
        var entry = Assert.Single(app.StatusHistory);
        Assert.Equal(ApplicationStatus.Wishlist, entry.FromStatus);
        Assert.Equal(ApplicationStatus.Applied, entry.ToStatus);
        Assert.Equal(Now, entry.ChangedAt);
    }

    [Fact]
    public void ChangeStatus_SameStatus_IsNoOp()
    {
        var app = NewApplication();
        app.ChangeStatus(ApplicationStatus.Applied, Now, Today);

        var changed = app.ChangeStatus(ApplicationStatus.Applied, Now.AddHours(1), Today);

        Assert.False(changed);
        Assert.Single(app.StatusHistory);
        Assert.Equal(Now, app.UpdatedAt);
    }

    [Fact]
    public void ChangeStatus_ToApplied_SetsAppliedOnWhenMissing()
    {
        var app = NewApplication();

        app.ChangeStatus(ApplicationStatus.Applied, Now, Today);

        Assert.Equal(new DateOnly(2026, 11, 21), app.AppliedOn);
    }

    [Fact]
    public void ChangeStatus_ToApplied_UsesLocalDateNotUtcDate()
    {
        var app = NewApplication();
        var lateUtcEvening = new DateTime(2026, 11, 21, 23, 30, 0, DateTimeKind.Utc);

        // In a UTC+1 zone it is already 22 November locally.
        app.ChangeStatus(ApplicationStatus.Applied, lateUtcEvening, new DateOnly(2026, 11, 22));

        Assert.Equal(new DateOnly(2026, 11, 22), app.AppliedOn);
        Assert.Equal(lateUtcEvening, app.StatusHistory.Single().ChangedAt);
    }

    [Fact]
    public void ChangeStatus_ToApplied_KeepsExistingAppliedOn()
    {
        var app = NewApplication();
        app.AppliedOn = new DateOnly(2026, 11, 1);

        app.ChangeStatus(ApplicationStatus.Applied, Now, Today);

        Assert.Equal(new DateOnly(2026, 11, 1), app.AppliedOn);
    }

    [Theory]
    [InlineData(ApplicationStatus.Screening)]
    [InlineData(ApplicationStatus.Interview)]
    [InlineData(ApplicationStatus.Offer)]
    public void ChangeStatus_SkippingPastApplied_SetsAppliedOnWhenMissing(ApplicationStatus later)
    {
        var app = NewApplication();

        app.ChangeStatus(later, Now, Today);

        Assert.Equal(Today, app.AppliedOn);
    }

    [Fact]
    public void ChangeStatus_SkippingPastApplied_UsesLocalDateNotUtcDate()
    {
        var app = NewApplication();
        var lateUtcEvening = new DateTime(2026, 11, 21, 23, 30, 0, DateTimeKind.Utc);

        app.ChangeStatus(ApplicationStatus.Interview, lateUtcEvening, new DateOnly(2026, 11, 22));

        Assert.Equal(new DateOnly(2026, 11, 22), app.AppliedOn);
    }

    [Fact]
    public void ChangeStatus_LaterStage_KeepsExistingAppliedOn()
    {
        var app = NewApplication();
        app.ChangeStatus(ApplicationStatus.Applied, Now, Today);

        app.ChangeStatus(ApplicationStatus.Interview, Now.AddDays(3), Today.AddDays(3));

        Assert.Equal(Today, app.AppliedOn);
    }

    [Theory]
    [InlineData(ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public void ChangeStatus_ClosingFromWishlist_LeavesAppliedOnEmpty(ApplicationStatus closed)
    {
        var app = NewApplication();

        app.ChangeStatus(closed, Now, Today);

        Assert.Null(app.AppliedOn);
    }

    [Theory]
    [InlineData(ApplicationStatus.Wishlist, false)]
    [InlineData(ApplicationStatus.Applied, true)]
    [InlineData(ApplicationStatus.Screening, true)]
    [InlineData(ApplicationStatus.Interview, true)]
    [InlineData(ApplicationStatus.Offer, true)]
    [InlineData(ApplicationStatus.Rejected, false)]
    [InlineData(ApplicationStatus.Withdrawn, false)]
    public void IsAppliedOrLater_covers_applied_and_later_open_stages(ApplicationStatus status, bool expected)
    {
        Assert.Equal(expected, status.IsAppliedOrLater());
    }

    [Fact]
    public void ChangeStatus_CanSkipStages()
    {
        var app = NewApplication();
        app.ChangeStatus(ApplicationStatus.Applied, Now, Today);

        app.ChangeStatus(ApplicationStatus.Interview, Now.AddDays(3), Today.AddDays(3));

        Assert.Equal(ApplicationStatus.Interview, app.CurrentStatus);
        Assert.Equal(2, app.StatusHistory.Count);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void ChangeStatus_NonUtcTimestamp_Throws(DateTimeKind kind)
    {
        var app = NewApplication();

        Assert.Throws<ArgumentException>(() =>
            app.ChangeStatus(ApplicationStatus.Applied, DateTime.SpecifyKind(Now, kind), Today));
    }

    [Theory]
    [InlineData(ApplicationStatus.Rejected, true)]
    [InlineData(ApplicationStatus.Withdrawn, true)]
    [InlineData(ApplicationStatus.Offer, false)]
    [InlineData(ApplicationStatus.Applied, false)]
    public void IsClosed_OnlyForTerminalStatuses(ApplicationStatus status, bool expected)
    {
        Assert.Equal(expected, status.IsClosed());
    }
}
