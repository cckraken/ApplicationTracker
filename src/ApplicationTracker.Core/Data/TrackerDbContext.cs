using ApplicationTracker.Core.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ApplicationTracker.Core.Data;

public class TrackerDbContext : DbContext
{
    private const string UnicodeLowerFunction = "unicode_lower";

    public TrackerDbContext(DbContextOptions<TrackerDbContext> options) : base(options)
    {
        // SQLite's lower() and LIKE only handle A-Z, so "MÜLLER" wouldn't match "Müller".
        // Register a .NET-backed lower-case function on the connection for case-insensitive
        // matching. Microsoft.Data.Sqlite applies it now if open, or when the connection opens.
        if (Database.GetDbConnection() is SqliteConnection sqlite)
        {
            sqlite.CreateFunction<string?, string?>(
                UnicodeLowerFunction, value => value?.ToLowerInvariant(), isDeterministic: true);
        }
    }

    /// <summary>
    /// Unicode-aware lower-casing usable inside LINQ queries (runs as SQL
    /// <c>unicode_lower()</c>). Compare against a value lowered with <c>ToLowerInvariant()</c>.
    /// </summary>
    public static string UnicodeLower(string value) => value.ToLowerInvariant();

    public DbSet<JobApplication> Applications => Set<JobApplication>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<StatusChange> StatusChanges => Set<StatusChange>();
    public DbSet<Reminder> Reminders => Set<Reminder>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // All enums are stored as readable strings.
        configurationBuilder.Properties<ApplicationStatus>().HaveConversion<string>().HaveMaxLength(20);

        // SQLite has no timezone; everything is stored as UTC and read back marked as UTC.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDbFunction(typeof(TrackerDbContext).GetMethod(nameof(UnicodeLower))!)
            .HasName(UnicodeLowerFunction);

        modelBuilder.Entity<JobApplication>(e =>
        {
            e.Property(a => a.RoleTitle).HasMaxLength(200);
            e.HasIndex(a => a.CurrentStatus);
            e.HasIndex(a => a.FollowUpOn);

            // Deleting a company must not silently drop its applications.
            e.HasOne(a => a.Company).WithMany(c => c.Applications)
                .HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Restrict);

            // Notes, history and reminders belong to the application and go with it.
            e.HasMany(a => a.Notes).WithOne(n => n.JobApplication)
                .HasForeignKey(n => n.JobApplicationId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(a => a.StatusHistory).WithOne(s => s.JobApplication)
                .HasForeignKey(s => s.JobApplicationId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(a => a.Reminders).WithOne(r => r.JobApplication)
                .HasForeignKey(r => r.JobApplicationId).OnDelete(DeleteBehavior.Cascade);

            e.HasMany(a => a.Contacts).WithMany(c => c.Applications)
                .UsingEntity(j => j.ToTable("ApplicationContacts"));
        });

        modelBuilder.Entity<Company>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(200);
            e.HasIndex(c => c.Name);
        });

        modelBuilder.Entity<Contact>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(200);
            e.HasOne(c => c.Company).WithMany(co => co.Contacts)
                .HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Note>(e => e.HasIndex(n => n.CreatedAt));

        modelBuilder.Entity<Reminder>(e =>
        {
            e.Property(r => r.Text).HasMaxLength(500);
            e.HasIndex(r => new { r.IsDone, r.DueOn });
        });
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}
