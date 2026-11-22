using System.ComponentModel.DataAnnotations;
using ApplicationTracker.Core.Applications;
using ApplicationTracker.Core.Domain;

namespace ApplicationTracker.Core.Reminders;

/// <summary>What the reminder form collects. Validated by <see cref="ReminderService"/>.</summary>
public class ReminderInput
{
    [Required(ErrorMessage = "Say what you need to do.")]
    [StringLength(Reminder.MaxTextLength)]
    [Display(Name = "Reminder")]
    public string Text { get; set; } = "";

    [Required(ErrorMessage = "Choose a due date.")]
    [Display(Name = "Due on")]
    public DateOnly? DueOn { get; set; }

    /// <summary>The application it is about; null for a reminder that stands on its own.</summary>
    [Display(Name = "Application")]
    public int? ApplicationId { get; set; }
}

/// <summary>
/// The application chosen for a reminder no longer exists. A <see cref="ValidationException"/>, so
/// it can be shown as a form error, but its own type, so a form without a picker can react to it.
/// </summary>
public class ApplicationNotFoundException()
    : ValidationException("That application no longer exists. Choose another, or none.");

/// <summary>The application a reminder is linked to, as shown beside it.</summary>
public sealed record ReminderApplication(int Id, string RoleTitle, string CompanyName);

/// <summary>One reminder as shown in lists. <see cref="CompletedAt"/> is UTC.</summary>
/// <param name="State">Overdue / due today / upcoming relative to today; None once done.</param>
public sealed record ReminderView(
    int Id,
    string Text,
    DateOnly DueOn,
    FollowUpState State,
    bool IsDone,
    DateTime? CompletedAt,
    ReminderApplication? Application);

/// <summary>An application that can be picked in the reminder form.</summary>
public sealed record ApplicationChoice(int Id, string RoleTitle, string CompanyName, bool IsClosed);

public sealed record ReminderFilter
{
    /// <summary>Only reminders for this application; null for all of them.</summary>
    public int? ApplicationId { get; init; }

    /// <summary>Only open reminders that are overdue or due today, and no done ones (the Today page).</summary>
    public bool DueOnly { get; init; }

    /// <summary>How many done reminders to return, most recently completed first.</summary>
    public int DoneLimit { get; init; } = 10;
}

/// <summary>Open reminders (earliest due first) and recently done ones (latest first).</summary>
public sealed record ReminderList(IReadOnlyList<ReminderView> Open, IReadOnlyList<ReminderView> Done);
