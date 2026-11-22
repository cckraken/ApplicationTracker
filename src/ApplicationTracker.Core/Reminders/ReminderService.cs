using System.ComponentModel.DataAnnotations;
using ApplicationTracker.Core.Applications;
using ApplicationTracker.Core.Data;
using ApplicationTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace ApplicationTracker.Core.Reminders;

/// <summary>Adds, edits, completes, snoozes and deletes reminders, linked to an application or not.</summary>
public class ReminderService(IDbContextFactory<TrackerDbContext> dbFactory, TimeProvider time)
{
    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    /// <summary>The user's local calendar date, used for due-ness.</summary>
    public DateOnly Today => time.GetLocalToday();

    public async Task<ReminderList> ListAsync(ReminderFilter filter, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var today = Today;

        IQueryable<Reminder> query = db.Reminders.AsNoTracking();
        if (filter.ApplicationId is int applicationId)
        {
            query = query.Where(r => r.JobApplicationId == applicationId);
        }

        var openQuery = query.Where(r => !r.IsDone);
        if (filter.DueOnly)
        {
            openQuery = openQuery.Where(r => r.DueOn <= today);
        }

        var open = await Project(openQuery.OrderBy(r => r.DueOn).ThenBy(r => r.Id)).ToListAsync(ct);

        var done = filter.DueOnly || filter.DoneLimit <= 0
            ? []
            : await Project(query.Where(r => r.IsDone)
                    .OrderByDescending(r => r.CompletedAt)
                    .ThenByDescending(r => r.Id)
                    .Take(filter.DoneLimit))
                .ToListAsync(ct);

        return new ReminderList(
            open.Select(r => r.ToView(today)).ToList(),
            done.Select(r => r.ToView(today)).ToList());
    }

    /// <summary>Every application, open ones first, for the form's application picker.</summary>
    public async Task<IReadOnlyList<ApplicationChoice>> GetApplicationChoicesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Applications.AsNoTracking()
            .OrderBy(a => TrackerDbContext.UnicodeLower(a.Company.Name))
            .ThenBy(a => TrackerDbContext.UnicodeLower(a.RoleTitle))
            .ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.RoleTitle, CompanyName = a.Company.Name, a.CurrentStatus })
            .ToListAsync(ct);

        return rows
            .Select(r => new ApplicationChoice(r.Id, r.RoleTitle, r.CompanyName, r.CurrentStatus.IsClosed()))
            .OrderBy(c => c.IsClosed) // stable, so the name order holds within each group
            .ToList();
    }

    /// <summary>The reminder's current values as form input, or null if it doesn't exist.</summary>
    public async Task<ReminderInput?> GetForEditAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Reminders.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new ReminderInput { Text = r.Text, DueOn = r.DueOn, ApplicationId = r.JobApplicationId })
            .SingleOrDefaultAsync(ct);
    }

    /// <returns>The new reminder's id.</returns>
    /// <exception cref="ValidationException">The input is invalid, or the chosen application no longer exists.</exception>
    public async Task<int> AddAsync(ReminderInput input, CancellationToken ct = default)
    {
        var (text, dueOn) = Validate(input);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await CheckApplicationExistsAsync(db, input.ApplicationId, ct);

        var reminder = new Reminder
        {
            Text = text,
            DueOn = dueOn,
            JobApplicationId = input.ApplicationId,
            CreatedAt = UtcNow,
        };
        db.Reminders.Add(reminder);
        await db.SaveChangesAsync(ct);
        return reminder.Id;
    }

    /// <summary>Replaces the text, due date and linked application. Done state is kept.</summary>
    /// <returns>False if the reminder doesn't exist.</returns>
    /// <exception cref="ValidationException">The input is invalid, or the chosen application no longer exists.</exception>
    public async Task<bool> UpdateAsync(int id, ReminderInput input, CancellationToken ct = default)
    {
        var (text, dueOn) = Validate(input);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var reminder = await db.Reminders.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (reminder is null)
        {
            return false;
        }

        await CheckApplicationExistsAsync(db, input.ApplicationId, ct);
        reminder.Text = text;
        reminder.DueOn = dueOn;
        reminder.JobApplicationId = input.ApplicationId;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Marks it done; already done is fine (keeps the first completion time).</summary>
    /// <returns>False if the reminder doesn't exist.</returns>
    public Task<bool> CompleteAsync(int id, CancellationToken ct = default) =>
        ChangeAsync(id, r => r.Complete(UtcNow), ct);

    /// <summary>Makes a done reminder open again with its old due date.</summary>
    /// <returns>False if the reminder doesn't exist.</returns>
    public Task<bool> ReopenAsync(int id, CancellationToken ct = default) =>
        ChangeAsync(id, r => r.Reopen(), ct);

    /// <summary>
    /// Moves the due date to tomorrow or a week from today. A done reminder is reopened:
    /// snoozing means "remind me again then".
    /// </summary>
    /// <returns>The new due date, or null if the reminder doesn't exist.</returns>
    public async Task<DateOnly?> SnoozeAsync(int id, SnoozePeriod period, CancellationToken ct = default)
    {
        var dueOn = period.DueFrom(Today);
        var found = await ChangeAsync(id, r =>
        {
            r.Reopen();
            r.DueOn = dueOn;
        }, ct);
        return found ? dueOn : null;
    }

    /// <returns>False if the reminder doesn't exist.</returns>
    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Reminders.Where(r => r.Id == id).ExecuteDeleteAsync(ct) > 0;
    }

    /// <summary>Uses the rules on <see cref="Reminder"/>, so there is one definition of overdue and due today.</summary>
    public static FollowUpState GetState(DateOnly dueOn, bool isDone, DateOnly today) =>
        isDone ? FollowUpState.None
        : Reminder.IsOverdueOn(dueOn, isDone, today) ? FollowUpState.Overdue
        : Reminder.IsDueTodayOn(dueOn, isDone, today) ? FollowUpState.DueToday
        : FollowUpState.Upcoming;

    private async Task<bool> ChangeAsync(int id, Action<Reminder> change, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var reminder = await db.Reminders.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (reminder is null)
        {
            return false;
        }

        change(reminder);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static async Task CheckApplicationExistsAsync(TrackerDbContext db, int? applicationId, CancellationToken ct)
    {
        if (applicationId is int id && !await db.Applications.AnyAsync(a => a.Id == id, ct))
        {
            throw new ApplicationNotFoundException();
        }
    }

    /// <summary>Checks the input and returns the trimmed text and the due date.</summary>
    private static (string Text, DateOnly DueOn) Validate(ReminderInput input)
    {
        Validator.ValidateObject(input, new ValidationContext(input), validateAllProperties: true);
        return (input.Text.Trim(), input.DueOn!.Value);
    }

    private static IQueryable<Row> Project(IQueryable<Reminder> query) =>
        query.Select(r => new Row(
            r.Id, r.Text, r.DueOn, r.IsDone, r.CompletedAt,
            r.JobApplication == null ? null : new ReminderApplication(
                r.JobApplication.Id, r.JobApplication.RoleTitle, r.JobApplication.Company.Name)));

    private sealed record Row(
        int Id, string Text, DateOnly DueOn, bool IsDone, DateTime? CompletedAt, ReminderApplication? Application)
    {
        public ReminderView ToView(DateOnly today) =>
            new(Id, Text, DueOn, GetState(DueOn, IsDone, today), IsDone, CompletedAt, Application);
    }
}
