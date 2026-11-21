namespace ApplicationTracker.Core.Domain;

/// <summary>A follow-up task, optionally tied to an application.</summary>
public class Reminder
{
    public int Id { get; set; }
    public int? JobApplicationId { get; set; }
    public JobApplication? JobApplication { get; set; }
    public required string Text { get; set; }
    public DateOnly DueOn { get; set; }
    public bool IsDone { get; set; }

    public bool IsOverdue(DateOnly today) => !IsDone && DueOn < today;

    public bool IsDueToday(DateOnly today) => !IsDone && DueOn == today;
}
