namespace ApplicationTracker.Core.Domain;

public class Company
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Website { get; set; }
    public string? Notes { get; set; }

    public List<JobApplication> Applications { get; } = [];
    public List<Contact> Contacts { get; } = [];
}
