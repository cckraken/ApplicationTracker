using ApplicationTracker.Core.Domain;

namespace ApplicationTracker.Tests.Domain;

public class ReminderTests
{
    private static readonly DateOnly Today = new(2026, 11, 21);

    [Theory]
    [InlineData(-1, false, true, false)] // yesterday, open -> overdue
    [InlineData(0, false, false, true)]  // today, open -> due today
    [InlineData(1, false, false, false)] // tomorrow -> neither
    [InlineData(-1, true, false, false)] // yesterday but done -> neither
    [InlineData(0, true, false, false)]  // today but done -> neither
    public void Overdue_and_due_today_flags(int dueOffsetDays, bool isDone, bool expectOverdue, bool expectDueToday)
    {
        var reminder = new Reminder
        {
            Text = "Follow up with recruiter",
            DueOn = Today.AddDays(dueOffsetDays),
        };
        if (isDone)
        {
            reminder.Complete(new DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc));
        }

        Assert.Equal(expectOverdue, reminder.IsOverdue(Today));
        Assert.Equal(expectDueToday, reminder.IsDueToday(Today));
    }

    private static readonly DateTime Noon = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);

    private static Reminder NewReminder() => new() { Text = "Follow up with recruiter", DueOn = Today };

    [Fact]
    public void A_new_reminder_is_open_with_no_completion_time()
    {
        var reminder = NewReminder();

        Assert.False(reminder.IsDone);
        Assert.Null(reminder.CompletedAt);
    }

    [Fact]
    public void Complete_marks_it_done_at_the_given_time()
    {
        var reminder = NewReminder();

        Assert.True(reminder.Complete(Noon));

        Assert.True(reminder.IsDone);
        Assert.Equal(Noon, reminder.CompletedAt);
    }

    [Fact]
    public void Completing_again_keeps_the_first_time_and_reports_no_change()
    {
        var reminder = NewReminder();
        reminder.Complete(Noon);

        Assert.False(reminder.Complete(Noon.AddHours(3)));

        Assert.True(reminder.IsDone);
        Assert.Equal(Noon, reminder.CompletedAt);
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void Complete_rejects_a_non_utc_time_and_stays_open(DateTimeKind kind)
    {
        var reminder = NewReminder();

        Assert.Throws<ArgumentException>(() => reminder.Complete(DateTime.SpecifyKind(Noon, kind)));

        Assert.False(reminder.IsDone);
        Assert.Null(reminder.CompletedAt);
    }

    [Fact]
    public void Complete_rejects_a_non_utc_time_even_when_already_done()
    {
        var reminder = NewReminder();
        reminder.Complete(Noon);

        Assert.Throws<ArgumentException>(() => reminder.Complete(DateTime.SpecifyKind(Noon, DateTimeKind.Local)));
        Assert.Equal(Noon, reminder.CompletedAt);
    }

    [Fact]
    public void Reopen_clears_done_and_completion_time_and_keeps_the_due_date()
    {
        var reminder = NewReminder();
        reminder.DueOn = Today.AddDays(-4);
        reminder.Complete(Noon);

        Assert.True(reminder.Reopen());

        Assert.False(reminder.IsDone);
        Assert.Null(reminder.CompletedAt);
        Assert.Equal(Today.AddDays(-4), reminder.DueOn);
        Assert.True(reminder.IsOverdue(Today));
    }

    [Fact]
    public void Reopening_an_open_reminder_reports_no_change()
    {
        var reminder = NewReminder();

        Assert.False(reminder.Reopen());
        Assert.False(reminder.IsDone);
    }

    [Fact]
    public void Completing_after_reopening_records_the_new_time()
    {
        var reminder = NewReminder();
        reminder.Complete(Noon);
        reminder.Reopen();

        Assert.True(reminder.Complete(Noon.AddDays(1)));

        Assert.Equal(Noon.AddDays(1), reminder.CompletedAt);
    }
}
