using System.ComponentModel.DataAnnotations;
using ApplicationTracker.Core.Contacts;
using ApplicationTracker.Core.Data;
using ApplicationTracker.Core.Domain;
using ApplicationTracker.Core.Notes;
using Microsoft.EntityFrameworkCore;

namespace ApplicationTracker.Core.Applications;

/// <summary>
/// All reads and writes of job applications go through here, so the UI stays thin
/// and every rule can be unit tested. Each call uses its own short-lived DbContext.
/// </summary>
public class ApplicationService(IDbContextFactory<TrackerDbContext> dbFactory, TimeProvider time)
{
    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    /// <summary>The user's local calendar date, used for follow-up urgency.</summary>
    public DateOnly Today => time.GetLocalToday();

    public async Task<IReadOnlyList<ApplicationSummary>> ListAsync(ApplicationFilter filter, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        IQueryable<JobApplication> query = db.Applications.AsNoTracking();

        if (filter.Status is ApplicationStatus status)
        {
            query = query.Where(a => a.CurrentStatus == status);
        }
        else if (!filter.IncludeClosed)
        {
            query = query.Where(a => a.CurrentStatus != ApplicationStatus.Rejected
                                  && a.CurrentStatus != ApplicationStatus.Withdrawn);
        }

        query = ApplySearch(query, filter.Search);
        query = Sort(query, filter.SortBy, filter.Descending);

        var rows = await query
            .Select(a => new
            {
                a.Id,
                CompanyName = a.Company.Name,
                a.RoleTitle,
                a.CurrentStatus,
                a.Location,
                a.AppliedOn,
                a.FollowUpOn,
            })
            .ToListAsync(ct);

        var today = Today;
        return rows
            .Select(r => new ApplicationSummary(
                r.Id, r.CompanyName, r.RoleTitle, r.CurrentStatus, r.Location, r.AppliedOn, r.FollowUpOn,
                GetFollowUpState(r.CurrentStatus, r.FollowUpOn, today)))
            .ToList();
    }

    /// <summary>
    /// Every application matching <paramref name="search"/>, grouped for the pipeline board:
    /// one column per open status (always present, in pipeline order) plus the closed ones.
    /// </summary>
    public async Task<BoardView> GetBoardAsync(string? search, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = ApplySearch(db.Applications.AsNoTracking(), search);

        var rows = await query
            .Select(a => new
            {
                a.Id,
                CompanyName = a.Company.Name,
                a.RoleTitle,
                a.CurrentStatus,
                a.Location,
                a.SalaryRange,
                a.AppliedOn,
                a.FollowUpOn,
                a.CreatedAt,
                LastChangedAt = a.StatusHistory
                    .OrderByDescending(s => s.ChangedAt)
                    .Select(s => (DateTime?)s.ChangedAt)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var today = Today;
        var cards = rows
            .Select(r => new
            {
                StageStartedUtc = r.LastChangedAt ?? r.CreatedAt,
                Card = new BoardCard(
                    r.Id, r.CompanyName, r.RoleTitle, r.CurrentStatus, r.Location, r.SalaryRange,
                    r.AppliedOn, r.FollowUpOn,
                    GetFollowUpState(r.CurrentStatus, r.FollowUpOn, today),
                    GetDaysInStage(r.LastChangedAt ?? r.CreatedAt, today)),
            })
            .ToList();

        var columns = ApplicationStatusExtensions.Pipeline
            .Select(status => new BoardColumn(status, OrderOpenCards(cards.Select(c => c.Card).Where(c => c.Status == status))))
            .ToList();

        // Most recently closed first (by exact time, not whole days): those are the ones
        // the user is most likely to look for.
        var closed = cards
            .Where(c => c.Card.Status.IsClosed())
            .OrderByDescending(c => c.StageStartedUtc)
            .ThenBy(c => c.Card.Id)
            .Select(c => c.Card)
            .ToList();

        return new BoardView(columns, closed);
    }

    /// <summary>
    /// Whole local calendar days since <paramref name="stageStartedUtc"/>; 0 on the day it started.
    /// Never negative, even if the clock went backwards. Unspecified kind is treated as UTC
    /// (as stored); a local time is rejected because it would be counted wrongly.
    /// </summary>
    public int GetDaysInStage(DateTime stageStartedUtc, DateOnly today)
    {
        if (stageStartedUtc.Kind == DateTimeKind.Local)
        {
            throw new ArgumentException("Timestamp must be UTC.", nameof(stageStartedUtc));
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(stageStartedUtc, DateTimeKind.Utc), time.LocalTimeZone);
        return Math.Max(0, today.DayNumber - DateOnly.FromDateTime(local).DayNumber);
    }

    /// <summary>
    /// Most urgent first: overdue (oldest first), due today, upcoming (soonest first), then no
    /// follow-up date. Ties go to the card that has waited longest in its stage.
    /// </summary>
    private static List<BoardCard> OrderOpenCards(IEnumerable<BoardCard> cards) =>
        cards
            .OrderBy(c => c.FollowUpState switch
            {
                FollowUpState.Overdue => 0,
                FollowUpState.DueToday => 1,
                FollowUpState.Upcoming => 2,
                _ => 3,
            })
            .ThenBy(c => c.FollowUpOn ?? DateOnly.MaxValue)
            .ThenByDescending(c => c.DaysInStage)
            .ThenBy(c => c.Id)
            .ToList();

    /// <summary>
    /// Everything the detail page shows: the application's fields, follow-up state, days in
    /// its current stage, notes (newest first) and linked contacts (by name). Null if it doesn't exist.
    /// </summary>
    public async Task<ApplicationDetails?> GetDetailsAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Applications.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new
            {
                a.Id,
                a.RoleTitle,
                CompanyName = a.Company.Name,
                a.CurrentStatus,
                a.PostingUrl,
                a.Location,
                a.SalaryRange,
                a.Source,
                a.AppliedOn,
                a.FollowUpOn,
                a.CreatedAt,
                a.UpdatedAt,
                LastChangedAt = a.StatusHistory
                    .OrderByDescending(s => s.ChangedAt)
                    .Select(s => (DateTime?)s.ChangedAt)
                    .FirstOrDefault(),
                Notes = a.Notes
                    .OrderByDescending(n => n.CreatedAt)
                    .ThenByDescending(n => n.Id)
                    .Select(n => new NoteView(n.Id, n.Body, n.CreatedAt, n.UpdatedAt))
                    .ToList(),
                Contacts = a.Contacts
                    .OrderBy(c => TrackerDbContext.UnicodeLower(c.Name))
                    .ThenBy(c => c.Id)
                    .Select(c => new ContactSummary(c.Id, c.Name, c.Role, c.Email, c.Phone, c.LinkedInUrl,
                        c.Company == null ? null : c.Company.Name))
                    .ToList(),
            })
            .AsSplitQuery()
            .SingleOrDefaultAsync(ct);

        if (row is null)
        {
            return null;
        }

        var today = Today;
        return new ApplicationDetails(
            row.Id, row.RoleTitle, row.CompanyName, row.CurrentStatus,
            row.PostingUrl, row.Location, row.SalaryRange, row.Source,
            row.AppliedOn, row.FollowUpOn,
            GetFollowUpState(row.CurrentStatus, row.FollowUpOn, today),
            GetDaysInStage(row.LastChangedAt ?? row.CreatedAt, today),
            row.CreatedAt, row.UpdatedAt,
            row.Notes, row.Contacts);
    }

    /// <summary>The application's current values as form input, or null if it doesn't exist.</summary>
    public async Task<ApplicationInput?> GetForEditAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Applications.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new ApplicationInput
            {
                Company = a.Company.Name,
                RoleTitle = a.RoleTitle,
                Status = a.CurrentStatus,
                OriginalStatus = a.CurrentStatus,
                PostingUrl = a.PostingUrl,
                Location = a.Location,
                SalaryRange = a.SalaryRange,
                Source = a.Source,
                AppliedOn = a.AppliedOn,
                FollowUpOn = a.FollowUpOn,
                PostingText = a.PostingText,
            })
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>Existing company names, for suggestions in the form.</summary>
    public async Task<IReadOnlyList<string>> GetCompanyNamesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Companies.AsNoTracking()
            .OrderBy(c => TrackerDbContext.UnicodeLower(c.Name))
            .Select(c => c.Name)
            .ToListAsync(ct);
    }

    /// <returns>The new application's id.</returns>
    /// <exception cref="ValidationException">The input is invalid.</exception>
    public async Task<int> CreateAsync(ApplicationInput input, CancellationToken ct = default)
    {
        Validate(input);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = UtcNow;

        var app = new JobApplication
        {
            Company = await CompanyLookup.FindOrCreateAsync(db, input.Company, ct),
            RoleTitle = input.RoleTitle.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
        ApplyFields(app, input);
        app.ChangeStatus(input.Status, now, Today);

        db.Applications.Add(app);
        await db.SaveChangesAsync(ct);
        return app.Id;
    }

    /// <returns>False if the application doesn't exist.</returns>
    /// <exception cref="ValidationException">The input is invalid.</exception>
    public async Task<bool> UpdateAsync(int id, ApplicationInput input, CancellationToken ct = default)
    {
        Validate(input);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = await db.Applications.Include(a => a.Company).SingleOrDefaultAsync(a => a.Id == id, ct);
        if (app is null)
        {
            return false;
        }

        var now = UtcNow;
        await ApplyCompanyChangeAsync(db, app, input.Company.Trim(), ct);

        app.RoleTitle = input.RoleTitle.Trim();
        ApplyFields(app, input);

        // Only act on the status if the user changed it in this form (see OriginalStatus).
        if (input.OriginalStatus is null || input.Status != input.OriginalStatus)
        {
            app.ChangeStatus(input.Status, now, Today);
        }
        app.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <returns>False if the application doesn't exist.</returns>
    public async Task<bool> ChangeStatusAsync(int id, ApplicationStatus status, CancellationToken ct = default) =>
        (await MoveToAsync(id, status, ct)).Outcome != MoveOutcome.NotFound;

    /// <summary>Moves the application to <paramref name="status"/>; a no-op if it's already there.</summary>
    public Task<MoveResult> MoveToAsync(int id, ApplicationStatus status, CancellationToken ct = default) =>
        MoveAsync(id, _ => status, ct);

    /// <summary>
    /// One stage back or forward along the pipeline, from the status as stored now (not as a
    /// possibly stale screen shows it). Unchanged at either end of the pipeline or when closed.
    /// </summary>
    public Task<MoveResult> MoveInPipelineAsync(int id, PipelineDirection direction, CancellationToken ct = default) =>
        MoveAsync(id, direction switch
        {
            PipelineDirection.Back => current => current.PreviousInPipeline(),
            PipelineDirection.Forward => current => current.NextInPipeline(),
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unknown direction."),
        }, ct);

    /// <summary>
    /// Closes the application as Rejected (the board's "drop on Closed"). An application that is
    /// already closed keeps its status, so Withdrawn isn't turned into Rejected.
    /// </summary>
    public Task<MoveResult> CloseAsync(int id, CancellationToken ct = default) =>
        MoveAsync(id, current => current.IsClosed() ? null : ApplicationStatus.Rejected, ct);

    /// <summary>
    /// Reads the stored status and applies the move in one context, so the target is always
    /// worked out from the current status.
    /// </summary>
    /// <param name="targetFor">The new status given the current one; null means stay.</param>
    private async Task<MoveResult> MoveAsync(
        int id, Func<ApplicationStatus, ApplicationStatus?> targetFor, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = await db.Applications.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (app is null)
        {
            return MoveResult.NotFound;
        }

        if (targetFor(app.CurrentStatus) is not ApplicationStatus target || !app.ChangeStatus(target, UtcNow, Today))
        {
            return MoveResult.Unchanged(app.CurrentStatus);
        }

        await db.SaveChangesAsync(ct);
        return MoveResult.Moved(app.CurrentStatus);
    }

    /// <summary>Permanently deletes the application with its notes, history and reminders.</summary>
    /// <returns>False if the application doesn't exist.</returns>
    public async Task<bool> DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deleted = await db.Applications.Where(a => a.Id == id).ExecuteDeleteAsync(ct);
        return deleted > 0;
    }

    /// <summary>
    /// "Followed up": clears the follow-up date. Only acts if the date is still
    /// <paramref name="expected"/>, so a stale screen can't wipe a date set elsewhere meanwhile.
    /// </summary>
    public Task<FollowUpActionOutcome> CompleteFollowUpAsync(int id, DateOnly expected, CancellationToken ct = default) =>
        ChangeFollowUpAsync(id, expected, newDate: null, ct);

    /// <summary>
    /// Moves the follow-up date to tomorrow or a week from today. Only acts if the date is still
    /// <paramref name="expected"/> (see <see cref="CompleteFollowUpAsync"/>).
    /// </summary>
    public Task<FollowUpActionOutcome> SnoozeFollowUpAsync(
        int id, DateOnly expected, SnoozePeriod period, CancellationToken ct = default) =>
        ChangeFollowUpAsync(id, expected, period.DueFrom(Today), ct);

    private async Task<FollowUpActionOutcome> ChangeFollowUpAsync(
        int id, DateOnly expected, DateOnly? newDate, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var app = await db.Applications.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (app is null)
        {
            return FollowUpActionOutcome.NotFound;
        }

        if (app.FollowUpOn != expected)
        {
            return FollowUpActionOutcome.Changed;
        }

        app.FollowUpOn = newDate;
        app.UpdatedAt = UtcNow;
        await db.SaveChangesAsync(ct);
        return FollowUpActionOutcome.Done;
    }

    public static FollowUpState GetFollowUpState(ApplicationStatus status, DateOnly? followUpOn, DateOnly today)
    {
        if (followUpOn is not DateOnly date || status.IsClosed())
        {
            return FollowUpState.None;
        }

        return date < today ? FollowUpState.Overdue
             : date == today ? FollowUpState.DueToday
             : FollowUpState.Upcoming;
    }

    private static void ApplyFields(JobApplication app, ApplicationInput input)
    {
        app.PostingUrl = Clean(input.PostingUrl);
        app.Location = Clean(input.Location);
        app.SalaryRange = Clean(input.SalaryRange);
        app.Source = Clean(input.Source);
        app.AppliedOn = input.AppliedOn;
        app.FollowUpOn = input.FollowUpOn;
        app.PostingText = Clean(input.PostingText);
    }

    private static async Task ApplyCompanyChangeAsync(TrackerDbContext db, JobApplication app, string newName, CancellationToken ct)
    {
        var current = app.Company;
        if (string.Equals(current.Name, newName, StringComparison.Ordinal))
        {
            return;
        }

        var usedElsewhere =
            await db.Applications.AnyAsync(a => a.CompanyId == current.Id && a.Id != app.Id, ct) ||
            await db.Contacts.AnyAsync(c => c.CompanyId == current.Id, ct);

        // Fixing only the capitalisation of a company used by this application alone: rename it.
        if (string.Equals(current.Name, newName, StringComparison.OrdinalIgnoreCase) && !usedElsewhere)
        {
            current.Name = newName;
            return;
        }

        var target = await CompanyLookup.FindOrCreateAsync(db, newName, ct);
        if (target.Id == current.Id)
        {
            return;
        }

        app.Company = target;

        // Don't leave a typo'd company behind in the suggestions.
        if (!usedElsewhere)
        {
            db.Companies.Remove(current);
        }
    }

    /// <summary>Matches company or role. Unicode-aware and literal: %, _ and \ have no special meaning.</summary>
    private static IQueryable<JobApplication> ApplySearch(IQueryable<JobApplication> query, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        var term = search.Trim().ToLowerInvariant();
        return query.Where(a => TrackerDbContext.UnicodeLower(a.Company.Name).Contains(term)
                             || TrackerDbContext.UnicodeLower(a.RoleTitle).Contains(term));
    }

    private static IQueryable<JobApplication> Sort(IQueryable<JobApplication> query, ApplicationSort sortBy, bool descending) =>
        sortBy switch
        {
            // Applications without a follow-up date go last in either direction.
            ApplicationSort.FollowUpOn => descending
                ? query.OrderBy(a => a.FollowUpOn == null).ThenByDescending(a => a.FollowUpOn).ThenBy(a => a.Id)
                : query.OrderBy(a => a.FollowUpOn == null).ThenBy(a => a.FollowUpOn).ThenBy(a => a.Id),
            ApplicationSort.Company => OrderBy(query, a => TrackerDbContext.UnicodeLower(a.Company.Name), descending),
            ApplicationSort.Role => OrderBy(query, a => TrackerDbContext.UnicodeLower(a.RoleTitle), descending),
            // Status is stored as text, so sort by pipeline position rather than alphabetically.
            ApplicationSort.Status => OrderBy(query, a =>
                a.CurrentStatus == ApplicationStatus.Wishlist ? 0 :
                a.CurrentStatus == ApplicationStatus.Applied ? 1 :
                a.CurrentStatus == ApplicationStatus.Screening ? 2 :
                a.CurrentStatus == ApplicationStatus.Interview ? 3 :
                a.CurrentStatus == ApplicationStatus.Offer ? 4 :
                a.CurrentStatus == ApplicationStatus.Rejected ? 5 : 6, descending),
            // Like follow-up, undated (not yet applied) rows go last in either direction.
            ApplicationSort.AppliedOn => descending
                ? query.OrderBy(a => a.AppliedOn == null).ThenByDescending(a => a.AppliedOn).ThenBy(a => a.Id)
                : query.OrderBy(a => a.AppliedOn == null).ThenBy(a => a.AppliedOn).ThenBy(a => a.Id),
            _ => OrderBy(query, a => a.UpdatedAt, descending),
        };

    private static IQueryable<JobApplication> OrderBy<TKey>(
        IQueryable<JobApplication> query, System.Linq.Expressions.Expression<Func<JobApplication, TKey>> key, bool descending) =>
        (descending ? query.OrderByDescending(key) : query.OrderBy(key)).ThenBy(a => a.Id);

    private static void Validate(ApplicationInput input) =>
        Validator.ValidateObject(input, new ValidationContext(input), validateAllProperties: true);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
