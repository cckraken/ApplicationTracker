namespace ApplicationTracker.Core.Domain;

/// <summary>A recruiter, hiring manager or interviewer.</summary>
public class Contact
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Their role, e.g. "Recruiter" or "Hiring manager".</summary>
    public string? Role { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? LinkedInUrl { get; set; }

    public int? CompanyId { get; set; }
    public Company? Company { get; set; }

    public List<JobApplication> Applications { get; } = [];
}
