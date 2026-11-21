namespace ApplicationTracker.Core.Domain;

/// <summary>One step in an application's pipeline history. Stats are computed from these.</summary>
public class StatusChange
{
    public int Id { get; set; }
    public int JobApplicationId { get; set; }
    public JobApplication JobApplication { get; set; } = null!;
    public ApplicationStatus FromStatus { get; set; }
    public ApplicationStatus ToStatus { get; set; }

    /// <summary>UTC.</summary>
    public DateTime ChangedAt { get; set; }
}
