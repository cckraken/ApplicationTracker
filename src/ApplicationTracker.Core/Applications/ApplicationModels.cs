using System.ComponentModel.DataAnnotations;
using ApplicationTracker.Core.Domain;

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
}

/// <summary>How urgent an application's follow-up date is, relative to today.</summary>
public enum FollowUpState
{
    None,
    Upcoming,
    DueToday,
    Overdue,
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
