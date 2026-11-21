namespace ApplicationTracker.Core.Domain;

public class Note
{
    public int Id { get; set; }
    public int JobApplicationId { get; set; }
    public JobApplication JobApplication { get; set; } = null!;
    public required string Body { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreatedAt { get; set; }
}
