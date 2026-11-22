namespace ApplicationTracker.Core;

public static class TimeProviderExtensions
{
    /// <summary>
    /// The user's local calendar date: what "today" means for follow-ups and reminders.
    /// Late in the evening or early in the morning it can differ from the UTC date.
    /// </summary>
    public static DateOnly GetLocalToday(this TimeProvider time) => DateOnly.FromDateTime(time.GetLocalNow().DateTime);
}
