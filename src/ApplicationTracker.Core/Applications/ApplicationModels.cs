using System.ComponentModel.DataAnnotations;
using ApplicationTracker.Core.Contacts;
using ApplicationTracker.Core.Domain;
using ApplicationTracker.Core.Notes;

namespace ApplicationTracker.Core.Applications;

/// <summary>What the add/edit form collects. Validated by <see cref="ApplicationService"/>.</summary>
public class ApplicationInput
{
    [Required(ErrorMessage = "Company is required.")]
    [StringLength(200)]
    public string Company { get; set; } = "";

    [Required(ErrorMessage = "Role is required.")]
    [StringLength(200)]
    [Display(Name = "Role")]
    public string RoleTitle { get; set; } = "";

    public ApplicationStatus Status { get; set; } = ApplicationStatus.Wishlist;

    /// <summary>
    /// The status when the form was loaded. Saving only changes the status when the user
    /// changed it, so a stale form can't undo a status change made elsewhere meanwhile.
    /// Null for new applications.
    /// </summary>
    public ApplicationStatus? OriginalStatus { get; set; }

    [Url(ErrorMessage = "Enter a full link, starting with http:// or https://.")]
    [StringLength(500)]
    [Display(Name = "Posting link")]
    public string? PostingUrl { get; set; }

    [StringLength(200)]
    public string? Location { get; set; }

    [StringLength(100)]
    [Display(Name = "Salary range")]
    public string? SalaryRange { get; set; }

    [StringLength(100)]
    public string? Source { get; set; }

    [Display(Name = "Applied on")]
    public DateOnly? AppliedOn { get; set; }

    [Display(Name = "Follow up on")]
    public DateOnly? FollowUpOn { get; set; }

    /// <summary>The job description, pasted from the posting.</summary>
    [StringLength(JobApplication.MaxPostingTextLength, ErrorMessage = "The job description is too long. Keep it under 20,000 characters, for example by leaving out the general \"about us\" text.")]
    [Display(Name = "Job description")]
    public string? PostingText { get; set; }
}

/// <summary>How urgent an application's follow-up date is, relative to today.</summary>
public enum FollowUpState
{
    None,
    Upcoming,
    DueToday,
    Overdue,
}

/// <summary>How far to push a follow-up or reminder back, counted from today.</summary>
public enum SnoozePeriod
{
    Tomorrow,
    NextWeek,
}

public static class SnoozePeriodExtensions
{
    /// <summary>The new due date: from today, not from the old date, so an overdue item lands in the future.</summary>
    public static DateOnly DueFrom(this SnoozePeriod period, DateOnly today) => period switch
    {
        SnoozePeriod.Tomorrow => today.AddDays(1),
        SnoozePeriod.NextWeek => today.AddDays(7),
        _ => throw new ArgumentOutOfRangeException(nameof(period), period, "Unknown snooze period."),
    };
}

/// <summary>What a follow-up action (done or snooze) did.</summary>
public enum FollowUpActionOutcome
{
    /// <summary>Saved.</summary>
    Done,

    /// <summary>The follow-up date was changed elsewhere since the page loaded; nothing was saved.</summary>
    Changed,

    /// <summary>The application doesn't exist (deleted elsewhere).</summary>
    NotFound,
}

/// <summary>One row of the applications list.</summary>
public sealed record ApplicationSummary(
    int Id,
    string CompanyName,
    string RoleTitle,
    ApplicationStatus Status,
    string? Location,
    DateOnly? AppliedOn,
    DateOnly? FollowUpOn,
    FollowUpState FollowUpState);

/// <summary>One application as shown on its detail page. Timestamps are UTC.</summary>
/// <param name="DaysInStage">Whole local days since the application entered its current status.</param>
/// <param name="Notes">Newest first.</param>
/// <param name="Contacts">Linked contacts, by name.</param>
public sealed record ApplicationDetails(
    int Id,
    string RoleTitle,
    string CompanyName,
    ApplicationStatus Status,
    string? PostingUrl,
    string? Location,
    string? SalaryRange,
    string? Source,
    DateOnly? AppliedOn,
    DateOnly? FollowUpOn,
    FollowUpState FollowUpState,
    int DaysInStage,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<NoteView> Notes,
    IReadOnlyList<ContactSummary> Contacts);

public enum PipelineDirection
{
    Back,
    Forward,
}

public enum MoveOutcome
{
    /// <summary>The status changed and was saved.</summary>
    Moved,

    /// <summary>Nothing to do: already there, or no further stage in that direction.</summary>
    Unchanged,

    /// <summary>The application doesn't exist (deleted elsewhere).</summary>
    NotFound,
}

/// <summary>What a move did. Create it with the factories, so Status is null only for NotFound.</summary>
/// <param name="Status">The status the application has now; null when it wasn't found.</param>
public sealed record MoveResult(MoveOutcome Outcome, ApplicationStatus? Status)
{
    public static MoveResult Moved(ApplicationStatus status) => new(MoveOutcome.Moved, status);
    public static MoveResult Unchanged(ApplicationStatus status) => new(MoveOutcome.Unchanged, status);
    public static MoveResult NotFound { get; } = new(MoveOutcome.NotFound, null);
}

/// <summary>One card on the pipeline board.</summary>
/// <param name="DaysInStage">Whole local days since the application entered its current status.</param>
public sealed record BoardCard(
    int Id,
    string CompanyName,
    string RoleTitle,
    ApplicationStatus Status,
    string? Location,
    string? SalaryRange,
    DateOnly? AppliedOn,
    DateOnly? FollowUpOn,
    FollowUpState FollowUpState,
    int DaysInStage);

/// <summary>One open-status column of the board, cards ordered most urgent first.</summary>
public sealed record BoardColumn(ApplicationStatus Status, IReadOnlyList<BoardCard> Cards);

/// <summary>
/// The pipeline board: a column per open status in pipeline order (empty ones included),
/// plus Rejected and Withdrawn applications together, most recently closed first.
/// </summary>
public sealed record BoardView(IReadOnlyList<BoardColumn> Columns, IReadOnlyList<BoardCard> Closed);

public enum ApplicationSort
{
    FollowUpOn,
    Company,
    Role,
    Status,
    AppliedOn,
    UpdatedAt,
}

public sealed record ApplicationFilter
{
    /// <summary>Matches company or role, case-insensitively.</summary>
    public string? Search { get; init; }

    /// <summary>Only this status. When set, closed applications are shown if it is a closed status.</summary>
    public ApplicationStatus? Status { get; init; }

    /// <summary>Include Rejected and Withdrawn when no specific status is chosen.</summary>
    public bool IncludeClosed { get; init; }

    public ApplicationSort SortBy { get; init; } = ApplicationSort.FollowUpOn;
    public bool Descending { get; init; }
}
