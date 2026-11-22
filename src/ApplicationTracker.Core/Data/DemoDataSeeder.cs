using ApplicationTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace ApplicationTracker.Core.Data;

/// <summary>
/// Fills an empty development database with fictional applications so the screens
/// have something to show. Never used against the real database.
/// </summary>
public static class DemoDataSeeder
{
    private sealed record Demo(
        string Company, string Role, ApplicationStatus Status, int? AppliedDaysAgo, int? FollowUpInDays,
        string? Location = null, string? Source = null);

    private static readonly Demo[] Demos =
    [
        new("Contoso", "Data Analyst", ApplicationStatus.Wishlist, null, null, "Remote", "LinkedIn"),
        new("Fabrikam", "Marketing Coordinator", ApplicationStatus.Wishlist, null, 3, "Hybrid", "Company site"),
        new("Northwind Traders", "Operations Assistant", ApplicationStatus.Applied, 12, -2, "On-site", "Indeed"),
        new("Adventure Works", "Customer Success Associate", ApplicationStatus.Applied, 7, 0, "Remote", "Referral"),
        new("Tailspin Toys", "Product Specialist", ApplicationStatus.Applied, 3, 7, "Hybrid", "LinkedIn"),
        new("Woodgrove Bank", "Junior Business Analyst", ApplicationStatus.Screening, 15, 2, "On-site", "Company site"),
        new("Litware", "Project Coordinator", ApplicationStatus.Screening, 10, -1, "Remote", "Recruiter"),
        new("Proseware", "UX Researcher", ApplicationStatus.Interview, 21, 1, "Hybrid", "LinkedIn"),
        new("Wide World Importers", "Supply Chain Analyst", ApplicationStatus.Interview, 18, null, "On-site", "Indeed"),
        new("Fourth Coffee", "Team Lead", ApplicationStatus.Offer, 30, 4, "On-site", "Referral"),
        new("Alpine Ski House", "Events Coordinator", ApplicationStatus.Rejected, 25, null, "On-site", "Company site"),
        new("Coho Winery", "Office Manager", ApplicationStatus.Withdrawn, 20, null, "Hybrid", "Indeed"),
    ];

    /// <summary>Adds the demo data if the database has no applications yet.</summary>
    public static async Task SeedIfEmptyAsync(TrackerDbContext db, TimeProvider time, CancellationToken ct = default)
    {
        if (await db.Applications.AnyAsync(ct))
        {
            return;
        }

        var utcNow = time.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);

        foreach (var demo in Demos)
        {
            var app = new JobApplication
            {
                Company = new Company { Name = demo.Company },
                RoleTitle = demo.Role,
                Location = demo.Location,
                Source = demo.Source,
                PostingUrl = $"https://example.com/jobs/{demo.Company.ToLowerInvariant().Replace(' ', '-')}",
                CreatedAt = utcNow.AddDays(-(demo.AppliedDaysAgo ?? 1) - 1),
                UpdatedAt = utcNow,
            };

            // Walk the pipeline so each demo application gets a realistic status history.
            if (demo.AppliedDaysAgo is int daysAgo)
            {
                var changedAt = utcNow.AddDays(-daysAgo);
                app.AppliedOn = today.AddDays(-daysAgo);
                foreach (var step in PathTo(demo.Status))
                {
                    app.ChangeStatus(step, changedAt, DateOnly.FromDateTime(changedAt));
                    changedAt = changedAt.AddDays(Math.Max(1, daysAgo / 4));
                }
            }

            if (demo.FollowUpInDays is int followUp)
            {
                app.FollowUpOn = today.AddDays(followUp);
            }

            if (demo.Status is ApplicationStatus.Interview or ApplicationStatus.Offer)
            {
                app.Notes.Add(new Note { Body = "Prepared answers for the competency questions.", CreatedAt = utcNow.AddDays(-2) });
            }

            if (demo.Status is ApplicationStatus.Screening or ApplicationStatus.Interview or ApplicationStatus.Offer)
            {
                app.Notes.Add(new Note
                {
                    Body = "Phone screen with the recruiter.\nAsked about notice period and salary expectations.",
                    CreatedAt = utcNow.AddDays(-5),
                    UpdatedAt = utcNow.AddDays(-4),
                });
                AddContacts(app, demo);
            }

            db.Applications.Add(app);
        }

        // One contact not linked to any application yet, as found on the Contacts page.
        db.Contacts.Add(new Contact { Name = "Robin Hale", Role = "Agency recruiter", Email = "robin.hale@example.com" });

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Fictional people for the later-stage demo applications.</summary>
    private static void AddContacts(JobApplication app, Demo demo)
    {
        var domain = $"{demo.Company.ToLowerInvariant().Replace(' ', '-')}.example.com";
        var people = new List<Contact>
        {
            new()
            {
                Name = $"Sam {demo.Company.Split(' ')[0]}",
                Role = "Recruiter",
                Email = $"sam@{domain}",
                Phone = "+44 20 7946 0000",
                Company = app.Company,
            },
        };

        if (demo.Status is ApplicationStatus.Interview or ApplicationStatus.Offer)
        {
            people.Add(new Contact
            {
                Name = "Jordan Lee",
                Role = "Hiring manager",
                LinkedInUrl = "https://www.linkedin.com/in/example",
                Company = app.Company,
            });
        }

        // One side of the many-to-many is enough; EF fills in the other.
        app.Contacts.AddRange(people);
    }

    private static IEnumerable<ApplicationStatus> PathTo(ApplicationStatus target) => target switch
    {
        ApplicationStatus.Applied => [ApplicationStatus.Applied],
        ApplicationStatus.Screening => [ApplicationStatus.Applied, ApplicationStatus.Screening],
        ApplicationStatus.Interview => [ApplicationStatus.Applied, ApplicationStatus.Screening, ApplicationStatus.Interview],
        ApplicationStatus.Offer => [ApplicationStatus.Applied, ApplicationStatus.Screening, ApplicationStatus.Interview, ApplicationStatus.Offer],
        ApplicationStatus.Rejected => [ApplicationStatus.Applied, ApplicationStatus.Screening, ApplicationStatus.Rejected],
        ApplicationStatus.Withdrawn => [ApplicationStatus.Applied, ApplicationStatus.Withdrawn],
        _ => [],
    };
}
