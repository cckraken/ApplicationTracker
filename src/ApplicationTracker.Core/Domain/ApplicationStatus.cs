namespace ApplicationTracker.Core.Domain;

/// <summary>Where a job application is in the pipeline. Stored as a string.</summary>
public enum ApplicationStatus
{
    Wishlist,
    Applied,
    Screening,
    Interview,
    Offer,
    Rejected,
    Withdrawn,
}

public static class ApplicationStatusExtensions
{
    /// <summary>True for statuses that end the pipeline (no further follow-up expected).</summary>
    public static bool IsClosed(this ApplicationStatus status) =>
        status is ApplicationStatus.Rejected or ApplicationStatus.Withdrawn;
}
