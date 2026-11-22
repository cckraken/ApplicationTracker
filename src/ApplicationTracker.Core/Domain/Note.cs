namespace ApplicationTracker.Core.Domain;

/// <summary>A free-text, timestamped note on an application.</summary>
public class Note
{
    /// <summary>Longest note body accepted (characters).</summary>
    public const int MaxBodyLength = 4000;

    public int Id { get; set; }
    public int JobApplicationId { get; set; }
    public JobApplication JobApplication { get; set; } = null!;
    public required string Body { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>UTC; null until the note is edited.</summary>
    public DateTime? UpdatedAt { get; set; }
}
