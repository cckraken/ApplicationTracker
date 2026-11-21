using ApplicationTracker.Core.Data;
using ApplicationTracker.Core.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ApplicationTracker.Tests.Data;

/// <summary>
/// Runs against real SQLite in memory (not the EF InMemory provider) using the
/// actual migrations, so schema, conversions and delete rules are exercised.
/// </summary>
public sealed class TrackerDbContextTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 21, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 21);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<TrackerDbContext> _options;

    public TrackerDbContextTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<TrackerDbContext>().UseSqlite(_connection).Options;

        using var db = NewContext();
        db.Database.Migrate();
    }

    public void Dispose() => _connection.Dispose();

    private TrackerDbContext NewContext() => new(_options);

    private int SeedApplication()
    {
        using var db = NewContext();
        var app = new JobApplication
        {
            RoleTitle = "Data Analyst",
            Company = new Company { Name = "Contoso" },
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        app.ChangeStatus(ApplicationStatus.Applied, Now, Today);
        app.Notes.Add(new Note { Body = "Applied via company site", CreatedAt = Now });
        app.Reminders.Add(new Reminder { Text = "Follow up", DueOn = new DateOnly(2026, 11, 28) });
        db.Applications.Add(app);
        db.SaveChanges();
        return app.Id;
    }

    [Fact]
    public void Migrations_have_no_pending_model_changes()
    {
        using var db = NewContext();

        Assert.False(db.Database.HasPendingModelChanges(),
            "The model has changed since the last migration. Run 'dotnet ef migrations add'.");
    }

    [Fact]
    public void Application_round_trips_with_status_history_and_utc_dates()
    {
        var id = SeedApplication();

        using var db = NewContext();
        var app = db.Applications
            .Include(a => a.StatusHistory)
            .Include(a => a.Company)
            .Single(a => a.Id == id);

        Assert.Equal(ApplicationStatus.Applied, app.CurrentStatus);
        Assert.Equal("Contoso", app.Company.Name);
        var change = Assert.Single(app.StatusHistory);
        Assert.Equal(DateTimeKind.Utc, change.ChangedAt.Kind);
        Assert.Equal(Now, change.ChangedAt);
        Assert.Equal(new DateOnly(2026, 11, 21), app.AppliedOn);
    }

    [Fact]
    public void Status_is_stored_as_text()
    {
        SeedApplication();

        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT CurrentStatus FROM Applications LIMIT 1";
        Assert.Equal("Applied", command.ExecuteScalar());
    }

    [Fact]
    public void Deleting_application_removes_its_notes_history_and_reminders()
    {
        var id = SeedApplication();

        using (var db = NewContext())
        {
            db.Applications.Remove(db.Applications.Single(a => a.Id == id));
            db.SaveChanges();
        }

        using var check = NewContext();
        Assert.Empty(check.Notes);
        Assert.Empty(check.StatusChanges);
        Assert.Empty(check.Reminders);
        Assert.Single(check.Companies);
    }

    [Fact]
    public void Deleting_company_with_applications_is_blocked()
    {
        SeedApplication();

        using var db = NewContext();
        db.Companies.Remove(db.Companies.Single());

        Assert.Throws<DbUpdateException>(() => db.SaveChanges());
    }
}
