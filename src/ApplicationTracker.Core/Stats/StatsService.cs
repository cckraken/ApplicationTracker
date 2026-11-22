using ApplicationTracker.Core.Data;
using ApplicationTracker.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace ApplicationTracker.Core.Stats;

/// <summary>Works out the stats dashboard from the applications and their status history.</summary>
public class StatsService(IDbContextFactory<TrackerDbContext> dbFactory, TimeProvider time)
{
    /// <summary>How many weeks "applications per week" covers, ending with the current week.</summary>
    public const int WeeksShown = 12;

    private static readonly ApplicationStatus[] ResponseStatuses =
        [ApplicationStatus.Screening, ApplicationStatus.Interview, ApplicationStatus.Offer];

    private static readonly ApplicationStatus[] AppliedStatuses = [ApplicationStatus.Applied, .. ResponseStatuses];

    public async Task<StatsDashboard> GetDashboardAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var applications = await db.Applications.AsNoTracking()
            .Select(a => new AppRow(a.Id, a.CurrentStatus, a.AppliedOn, a.CreatedAt))
            .ToListAsync(ct);

        var changes = await db.StatusChanges.AsNoTracking()
            .OrderBy(c => c.ChangedAt).ThenBy(c => c.Id)
            .Select(c => new ChangeRow(c.JobApplicationId, c.FromStatus, c.ToStatus, c.ChangedAt))
            .ToListAsync(ct);

        var historyByApp = changes.ToLookup(c => c.ApplicationId);
        var today = time.GetLocalToday();

        return new StatsDashboard(
            applications.Count,
            CountByStatus(applications),
            GetResponseRate(applications, historyByApp),
            CountPerWeek(applications, today),
            GetTimeInStage(applications, historyByApp));
    }

    private static List<StatusCount> CountByStatus(List<AppRow> applications)
    {
        var counts = applications.GroupBy(a => a.Status).ToDictionary(g => g.Key, g => g.Count());
        return ApplicationStatusExtensions.Pipeline
            .Concat([ApplicationStatus.Rejected, ApplicationStatus.Withdrawn])
            .Select(s => new StatusCount(s, counts.GetValueOrDefault(s)))
            .ToList();
    }

    private static ResponseRate GetResponseRate(List<AppRow> applications, ILookup<int, ChangeRow> historyByApp)
    {
        var applied = 0;
        var responded = 0;
        foreach (var app in applications)
        {
            var history = historyByApp[app.Id];
            if (app.AppliedOn is null && !history.Any(c => AppliedStatuses.Contains(c.To)))
            {
                continue;
            }

            applied++;
            if (history.Any(c => ResponseStatuses.Contains(c.To)))
            {
                responded++;
            }
        }

        return new ResponseRate(responded, applied, applied == 0 ? null : (double)responded / applied);
    }

    private static List<WeekCount> CountPerWeek(List<AppRow> applications, DateOnly today)
    {
        var currentWeek = WeekStart(today);
        var firstWeek = currentWeek.AddDays(-7 * (WeeksShown - 1));
        var counts = applications
            .Where(a => a.AppliedOn is DateOnly d && d >= firstWeek && d < currentWeek.AddDays(7))
            .GroupBy(a => WeekStart(a.AppliedOn!.Value))
            .ToDictionary(g => g.Key, g => g.Count());

        return Enumerable.Range(0, WeeksShown)
            .Select(i => firstWeek.AddDays(7 * i))
            .Select(w => new WeekCount(w, counts.GetValueOrDefault(w)))
            .ToList();
    }

    /// <summary>The Monday on or before <paramref name="date"/>.</summary>
    public static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static List<StageDuration> GetTimeInStage(List<AppRow> applications, ILookup<int, ChangeRow> historyByApp)
    {
        var stays = new Dictionary<ApplicationStatus, List<double>>();
        foreach (var app in applications)
        {
            var history = historyByApp[app.Id].ToList();
            if (history.Count == 0)
            {
                continue; // still in its first stage: no finished stay
            }

            // The first stage runs from creation to the first change; each later stage from one change to the next.
            var stage = history[0].From;
            var enteredAt = app.CreatedAt;
            foreach (var change in history)
            {
                var days = (change.ChangedAt - enteredAt).TotalDays;
                if (days > 0)
                {
                    (stays.TryGetValue(stage, out var list) ? list : stays[stage] = []).Add(days);
                }

                stage = change.To;
                enteredAt = change.ChangedAt;
            }
        }

        return ApplicationStatusExtensions.Pipeline
            .Select(s => stays.TryGetValue(s, out var list)
                ? new StageDuration(s, list.Average(), list.Count)
                : new StageDuration(s, null, 0))
            .ToList();
    }

    private sealed record AppRow(int Id, ApplicationStatus Status, DateOnly? AppliedOn, DateTime CreatedAt);

    private sealed record ChangeRow(int ApplicationId, ApplicationStatus From, ApplicationStatus To, DateTime ChangedAt);
}
