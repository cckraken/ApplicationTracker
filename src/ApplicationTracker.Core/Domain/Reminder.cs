namespace ApplicationTracker.Core.Domain;

/// <summary>A follow-up task, optionally tied to an application.</summary>
public class Reminder
{
    public const int MaxTextLength = 500;

    public int Id { get; set; }
    public int? JobApplicationId { get; set; }
    public JobApplication? JobApplication { get; set; }
    public required string Text { get; set; }
    public DateOnly DueOn { get; set; }
    public bool IsDone { get; private set; }

    /// <summary>UTC.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When it was marked done, in UTC; null while open.</summary>
    public DateTime? CompletedAt { get; private set; }

    public bool IsOverdue(DateOnly today) => IsOverdueOn(DueOn, IsDone, today);

    public bool IsDueToday(DateOnly today) => IsDueTodayOn(DueOn, IsDone, today);

    /// <summary>The overdue rule, for code that has the values but not the entity (e.g. query projections).</summary>
    public static bool IsOverdueOn(DateOnly dueOn, bool isDone, DateOnly today) => !isDone && dueOn < today;

    /// <summary>The due-today rule, for code that has the values but not the entity.</summary>
    public static bool IsDueTodayOn(DateOnly dueOn, bool isDone, DateOnly today) => !isDone && dueOn == today;

    /// <summary>Marks the reminder done. Completing one that is already done keeps the first time.</summary>
    /// <returns>True if it changed.</returns>
    public bool Complete(DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(utcNow));
        }

        if (IsDone)
        {
            return false;
        }

        IsDone = true;
        CompletedAt = utcNow;
        return true;
    }

    /// <summary>Makes a done reminder open again, keeping its due date.</summary>
    /// <returns>True if it changed.</returns>
    public bool Reopen()
    {
        if (!IsDone)
        {
            return false;
        }

        IsDone = false;
        CompletedAt = null;
        return true;
    }
}
