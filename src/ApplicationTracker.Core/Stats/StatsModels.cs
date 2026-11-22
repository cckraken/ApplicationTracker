using ApplicationTracker.Core.Domain;

namespace ApplicationTracker.Core.Stats;

/// <summary>Every figure on the stats dashboard, all time.</summary>
/// <param name="TotalApplications">Every application, whatever its status.</param>
/// <param name="ByStatus">One entry per status (zeros included): pipeline order, then Rejected and Withdrawn.</param>
/// <param name="PerWeek">The last <see cref="StatsService.WeeksShown"/> weeks (Monday start), oldest first, ending with the current week.</param>
/// <param name="TimeInStage">One entry per open pipeline stage, in pipeline order.</param>
public sealed record StatsDashboard(
    int TotalApplications,
    IReadOnlyList<StatusCount> ByStatus,
    ResponseRate Response,
    IReadOnlyList<WeekCount> PerWeek,
    IReadOnlyList<StageDuration> TimeInStage);

/// <summary>How many applications are currently in <paramref name="Status"/>.</summary>
public sealed record StatusCount(ApplicationStatus Status, int Count);

/// <summary>
/// Applications that got past Applied. <paramref name="Applied"/> counts applications that were ever
/// applied to (an applied-on date, or ever reached Applied or a later open stage; closing straight from
/// Wishlist is not applying); <paramref name="Responded"/> counts those that ever reached Screening,
/// Interview or Offer. A rejection straight from Applied is not a response.
/// </summary>
/// <param name="Rate">Responded / Applied between 0 and 1, or null when nothing has been applied to yet.</param>
public sealed record ResponseRate(int Responded, int Applied, double? Rate);

/// <summary>Applications whose applied-on date falls in the week starting <paramref name="WeekStart"/> (a Monday).</summary>
public sealed record WeekCount(DateOnly WeekStart, int Count);

/// <summary>
/// Average length of a finished stay in <paramref name="Status"/>. A stay starts when the application enters
/// the stage (its creation for the first stage) and ends at the next status change; the current, unfinished
/// stay is not counted, nor are zero-length stays. Repeat visits count as separate stays.
/// </summary>
/// <param name="AverageDays">Null when there are no finished stays.</param>
/// <param name="Stays">How many finished stays the average is over.</param>
public sealed record StageDuration(ApplicationStatus Status, double? AverageDays, int Stays);
