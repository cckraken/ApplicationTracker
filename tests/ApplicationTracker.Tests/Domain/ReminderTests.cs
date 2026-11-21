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
            IsDone = isDone,
        };

        Assert.Equal(expectOverdue, reminder.IsOverdue(Today));
        Assert.Equal(expectDueToday, reminder.IsDueToday(Today));
    }
}
