using ApplicationTracker.Core.Domain;

namespace ApplicationTracker.Tests.Domain;

public class JobApplicationTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 9, 30, 0, DateTimeKind.Utc);

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

        var changed = app.ChangeStatus(ApplicationStatus.Applied, Now);

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
        app.ChangeStatus(ApplicationStatus.Applied, Now);

        var changed = app.ChangeStatus(ApplicationStatus.Applied, Now.AddHours(1));

        Assert.False(changed);
        Assert.Single(app.StatusHistory);
        Assert.Equal(Now, app.UpdatedAt);
    }

    [Fact]
    public void ChangeStatus_ToApplied_SetsAppliedOnWhenMissing()
    {
        var app = NewApplication();

        app.ChangeStatus(ApplicationStatus.Applied, Now);

        Assert.Equal(new DateOnly(2026, 11, 21), app.AppliedOn);
    }

    [Fact]
    public void ChangeStatus_ToApplied_KeepsExistingAppliedOn()
    {
        var app = NewApplication();
        app.AppliedOn = new DateOnly(2026, 11, 1);

        app.ChangeStatus(ApplicationStatus.Applied, Now);

        Assert.Equal(new DateOnly(2026, 11, 1), app.AppliedOn);
    }

    [Fact]
    public void ChangeStatus_CanSkipStages()
    {
        var app = NewApplication();
        app.ChangeStatus(ApplicationStatus.Applied, Now);

        app.ChangeStatus(ApplicationStatus.Interview, Now.AddDays(3));

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
            app.ChangeStatus(ApplicationStatus.Applied, DateTime.SpecifyKind(Now, kind)));
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
