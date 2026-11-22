using System.ComponentModel.DataAnnotations;
using ApplicationTracker.Core.Domain;

namespace ApplicationTracker.Core.Contacts;

/// <summary>What the contact form collects. Validated by <see cref="ContactService"/>.</summary>
public class ContactInput
{
    [Required(ErrorMessage = "Name is required.")]
    [StringLength(200)]
    public string Name { get; set; } = "";

    /// <summary>E.g. "Recruiter", "Hiring manager", "Interviewer".</summary>
    [StringLength(100)]
    public string? Role { get; set; }

    // Email and LinkedIn are format-checked, so they are trimmed as they are set: the form's own
    // validation then agrees with ContactService, and a pasted " https://..." is accepted.
    private string? email;
    private string? linkedInUrl;

    [EmailAddress(ErrorMessage = "Enter an email address like name@example.com.")]
    [StringLength(200)]
    public string? Email
    {
        get => email;
        set => email = value?.Trim();
    }

    [StringLength(50)]
    public string? Phone { get; set; }

    [Url(ErrorMessage = "Enter a full link, starting with http:// or https://.")]
    [StringLength(500)]
    [Display(Name = "LinkedIn profile")]
    public string? LinkedInUrl
    {
        get => linkedInUrl;
        set => linkedInUrl = value?.Trim();
    }

    /// <summary>Company name; matched to an existing company ignoring case, or created.</summary>
    [StringLength(200)]
    public string? Company { get; set; }
}

/// <summary>A contact's details as shown in lists and on the detail page.</summary>
public sealed record ContactSummary(
    int Id,
    string Name,
    string? Role,
    string? Email,
    string? Phone,
    string? LinkedInUrl,
    string? CompanyName);

/// <summary>An application a contact is linked to.</summary>
public sealed record LinkedApplication(int Id, string RoleTitle, string CompanyName, ApplicationStatus Status);

/// <summary>One row of the Contacts page.</summary>
public sealed record ContactListItem(ContactSummary Contact, IReadOnlyList<LinkedApplication> Applications);
