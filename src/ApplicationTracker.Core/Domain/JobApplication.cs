namespace ApplicationTracker.Core.Domain;

/// <summary>A single job the user is tracking, from wishlist through to an outcome.</summary>
public class JobApplication
{
    public int Id { get; set; }

    public int CompanyId { get; set; }
    public Company Company { get; set; } = null!;

    public required string RoleTitle { get; set; }
    public string? PostingUrl { get; set; }
    public string? Location { get; set; }
    public string? SalaryRange { get; set; }

    /// <summary>Where the posting came from, e.g. job board, referral, company site.</summary>
    public string? Source { get; set; }

    public DateOnly? AppliedOn { get; set; }
    public DateOnly? FollowUpOn { get; set; }

    public ApplicationStatus CurrentStatus { get; private set; } = ApplicationStatus.Wishlist;

    /// <summary>UTC.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC.</summary>
    public DateTime UpdatedAt { get; set; }

    public List<StatusChange> StatusHistory { get; } = [];
    public List<Note> Notes { get; } = [];
    public List<Reminder> Reminders { get; } = [];
    public List<Contact> Contacts { get; } = [];

    /// <summary>
    /// Moves the application to <paramref name="newStatus"/> and records the change in
    /// <see cref="StatusHistory"/>. Moving to the current status is a no-op.
    /// Any transition is allowed: real hiring processes skip and repeat stages.
    /// </summary>
    /// <param name="utcNow">When the change happened, in UTC (stored in the history).</param>
    /// <param name="localToday">
    /// The user's local calendar date, used to fill in <see cref="AppliedOn"/>. This is passed
    /// separately because the UTC date can differ from the local date late in the evening or
    /// early in the morning.
    /// </param>
    /// <returns>True if the status changed.</returns>
    public bool ChangeStatus(ApplicationStatus newStatus, DateTime utcNow, DateOnly localToday)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(utcNow));
        }

        if (newStatus == CurrentStatus)
        {
            return false;
        }

        StatusHistory.Add(new StatusChange
        {
            FromStatus = CurrentStatus,
            ToStatus = newStatus,
            ChangedAt = utcNow,
        });

        if (newStatus == ApplicationStatus.Applied && AppliedOn is null)
        {
            AppliedOn = localToday;
        }

        CurrentStatus = newStatus;
        UpdatedAt = utcNow;
        return true;
    }
}
