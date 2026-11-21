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
    /// <summary>The open statuses, in pipeline order (the board's columns).</summary>
    public static IReadOnlyList<ApplicationStatus> Pipeline => PipelineOrder;

    private static readonly ApplicationStatus[] PipelineOrder =
    [
        ApplicationStatus.Wishlist,
        ApplicationStatus.Applied,
        ApplicationStatus.Screening,
        ApplicationStatus.Interview,
        ApplicationStatus.Offer,
    ];

    /// <summary>The previous open status, or null at the start of the pipeline or for a closed status.</summary>
    public static ApplicationStatus? PreviousInPipeline(this ApplicationStatus status)
    {
        var index = IndexOf(status);
        return index > 0 ? Pipeline[index - 1] : null;
    }

    /// <summary>The next open status, or null at the end of the pipeline or for a closed status.</summary>
    public static ApplicationStatus? NextInPipeline(this ApplicationStatus status)
    {
        var index = IndexOf(status);
        return index >= 0 && index < Pipeline.Count - 1 ? Pipeline[index + 1] : null;
    }

    private static int IndexOf(ApplicationStatus status) => Array.IndexOf(PipelineOrder, status);

    /// <summary>True for statuses that end the pipeline (no further follow-up expected).</summary>
    public static bool IsClosed(this ApplicationStatus status) =>
        status is ApplicationStatus.Rejected or ApplicationStatus.Withdrawn;
}
