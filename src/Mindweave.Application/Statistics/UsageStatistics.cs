using Mindweave.Application.Storage;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Statistics;

/// <summary>The statistics page's period: the last 30 local days by day, or the last 12 local months by month.</summary>
public enum UsagePeriod
{
    Daily,
    Monthly,
}

/// <summary>What a request was made for (用途): answers and the background work beside them.</summary>
public enum UsagePurpose
{
    Answer,
    Title,
    Summary,
    Naming,
    WorkspaceName,

    /// <summary>Recommended questions and nodes (requirements §10.3), asked for or prepared in the background.</summary>
    Recommendation,

    /// <summary>A kind this build does not know (written by a newer one); counted, never hidden.</summary>
    Other,
}

/// <summary>
/// Tokens as the services reported them. <see cref="Requests"/> counts the requests whose usage was reported (their
/// tokens are in the sums); <see cref="Unreported"/> the ones whose service returned none, which are counted apart and
/// never filled in with the app's own estimate.
/// </summary>
public readonly record struct TokenCounts(long Input, long Output, int Requests, int Unreported)
{
    public long Total => Input + Output;

    public static TokenCounts operator +(TokenCounts left, TokenCounts right) =>
        new(left.Input + right.Input, left.Output + right.Output, left.Requests + right.Requests, left.Unreported + right.Unreported);

    public TokenCounts Add(TokenCounts other) => this + other;
}

/// <summary>A model as the statistics and prices tell them apart: its name and the host of the service address.</summary>
public sealed record ModelKey(string Model, string Host)
{
    /// <summary>The host of a service address (with a non-default port), lower case; the address itself when it is not a URL.</summary>
    public static string HostOf(string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        return Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) && uri.Host.Length > 0
            ? uri.IsDefaultPort ? uri.Host.ToLowerInvariant() : $"{uri.Host.ToLowerInvariant()}:{uri.Port}"
            : baseUrl.Trim();
    }

    public static ModelKey From(string model, string baseUrl)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new(model, HostOf(baseUrl));
    }
}

/// <summary>One bar of the chart: a local day, or a month starting on <see cref="Start"/>.</summary>
public sealed record UsageBucket(DateOnly Start, TokenCounts Counts);

public sealed record ModelUsage(ModelKey Key, TokenCounts Counts);

public sealed record PurposeUsage(UsagePurpose Purpose, TokenCounts Counts);

/// <summary>
/// Token use in the scope for the chosen period: every bucket from <see cref="From"/> to <see cref="To"/> (empty ones
/// too), the totals, and the same split by model and by purpose. <see cref="RequestsEver"/> counts the scope's requests
/// of any time, to tell "none yet" from "none in this period".
/// </summary>
public sealed record UsageReport(
    UsagePeriod Period,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<UsageBucket> Buckets,
    TokenCounts Totals,
    IReadOnlyList<ModelUsage> ByModel,
    IReadOnlyList<PurposeUsage> ByPurpose,
    int RequestsEver);

public enum MilestoneKind
{
    Question,
    Summary,
}

/// <summary>The <see cref="Count"/>th question or summary was made on <see cref="Day"/> (local).</summary>
public sealed record Milestone(MilestoneKind Kind, int Count, DateOnly Day);

/// <summary>
/// Plain counts of what is there now in the scope (deleted content is not counted), the local days with at least one
/// question, and the dates the thresholds were reached. Facts only: nothing here rates learning.
/// </summary>
public sealed record ActivityFacts(
    int Questions,
    int Conversations,
    int Branches,
    int Summaries,
    int SummaryVersions,
    int ReadSummaries,
    int ArchivedConversations,
    int ActiveDays,
    int RecentActiveDays,
    DateOnly? FirstActiveDay,
    IReadOnlyList<Milestone> Milestones);

/// <summary>Everything the statistics page shows for one scope and period, as of <see cref="Today"/> (local).</summary>
public sealed record StatisticsReport(UsageReport Usage, ActivityFacts Activity, bool HasModel, DateOnly Today);

/// <summary>The statistics page's figures (2026-11-12), read with aggregate queries off the UI thread.</summary>
public interface IUsageStatistics
{
    /// <summary>The figures for one workspace, or for all of them when <paramref name="workspace"/> is null.</summary>
    Task<StatisticsReport> LoadAsync(WorkspaceId? workspace, UsagePeriod period, CancellationToken cancellationToken);
}

/// <summary>The clock and zone the statistics count days by; tests give their own, the app the machine's.</summary>
public sealed class UsageStatisticsOptions
{
    public TimeProvider? Time { get; init; }

    /// <summary>The zone local days are counted in; the clock's own local zone when null.</summary>
    public TimeZoneInfo? Zone { get; init; }
}

/// <summary>
/// Turns the store's per-day rows into the page's buckets, totals and splits. Days are local to the zone; a request
/// counts on the day it started.
/// </summary>
public sealed class UsageStatisticsService(IStatisticsStore store, IModelSettingsStore models, TimeProvider time, UsageStatisticsOptions options) : IUsageStatistics
{
    /// <summary>Questions counted as milestones once reached.</summary>
    public static IReadOnlyList<int> QuestionThresholds { get; } = [1, 10, 50, 100, 500, 1000];

    /// <summary>Summaries counted as milestones once reached.</summary>
    public static IReadOnlyList<int> SummaryThresholds { get; } = [1, 10, 50, 100];

    /// <summary>Days in the daily chart and in "active in the last 30 days".</summary>
    public const int RecentDays = 30;

    /// <summary>Months in the monthly chart.</summary>
    public const int RecentMonths = 12;

    public async Task<StatisticsReport> LoadAsync(WorkspaceId? workspace, UsagePeriod period, CancellationToken cancellationToken)
    {
        var clock = options.Time ?? time;
        var zone = options.Zone ?? clock.LocalTimeZone;
        var today = LocalCalendar.DayOf(clock.GetUtcNow(), zone);
        var (from, to) = Window(period, today);
        var recentFrom = today.AddDays(1 - RecentDays);

        var rows = await store.UsageByDayAsync(workspace, LocalCalendar.StartOf(from, zone), LocalCalendar.StartOf(to.AddDays(1), zone), zone, cancellationToken).ConfigureAwait(false);
        var activity = await store.ActivityAsync(
            new ActivityQuery(workspace, zone, LocalCalendar.StartOf(recentFrom, zone), QuestionThresholds, SummaryThresholds),
            cancellationToken).ConfigureAwait(false);
        var hasModel = await models.GetActiveAsync(cancellationToken).ConfigureAwait(false) is not null;
        return new StatisticsReport(Summarize(period, from, to, rows), activity, hasModel, today);
    }

    /// <summary>The first and last day the period covers, both included: 30 days, or 12 whole months, ending today.</summary>
    public static (DateOnly From, DateOnly To) Window(UsagePeriod period, DateOnly today) => period switch
    {
        UsagePeriod.Daily => (today.AddDays(1 - RecentDays), today),
        _ => (LocalCalendar.MonthOf(today).AddMonths(1 - RecentMonths), today),
    };

    /// <summary>Buckets every day (or month) of the window, empty ones included, and splits the same rows by model and purpose.</summary>
    public static UsageReport Summarize(UsagePeriod period, DateOnly from, DateOnly to, UsageRows rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var inWindow = rows.Rows.Where(r => r.Day >= from && r.Day <= to).ToList();
        var buckets = new List<UsageBucket>();
        var byStart = new Dictionary<DateOnly, TokenCounts>();
        foreach (var row in inWindow)
        {
            var start = period == UsagePeriod.Daily ? row.Day : LocalCalendar.MonthOf(row.Day);
            byStart[start] = byStart.GetValueOrDefault(start) + row.Counts;
        }

        for (var start = period == UsagePeriod.Daily ? from : LocalCalendar.MonthOf(from);
             start <= to;
             start = period == UsagePeriod.Daily ? start.AddDays(1) : start.AddMonths(1))
        {
            buckets.Add(new UsageBucket(start, byStart.GetValueOrDefault(start)));
        }

        var totals = inWindow.Aggregate(default(TokenCounts), (sum, row) => sum + row.Counts);
        var byModel = inWindow
            .GroupBy(r => ModelKey.From(r.Model, r.BaseUrl))
            .Select(g => new ModelUsage(g.Key, g.Aggregate(default(TokenCounts), (sum, row) => sum + row.Counts)))
            .OrderByDescending(m => m.Counts.Total).ThenByDescending(m => m.Counts.Requests + m.Counts.Unreported)
            .ThenBy(m => m.Key.Model, StringComparer.Ordinal).ThenBy(m => m.Key.Host, StringComparer.Ordinal)
            .ToList();
        var purposes = inWindow
            .GroupBy(r => PurposeOf(r.Kind))
            .ToDictionary(g => g.Key, g => g.Aggregate(default(TokenCounts), (sum, row) => sum + row.Counts));
        var byPurpose = Enum.GetValues<UsagePurpose>()
            .Where(p => p != UsagePurpose.Other || purposes.ContainsKey(p))
            .Select(p => new PurposeUsage(p, purposes.GetValueOrDefault(p)))
            .ToList();
        return new UsageReport(period, from, to, buckets, totals, byModel, byPurpose, rows.RequestsEver);
    }

    /// <summary>The stored kind of a request: a task's kind, or the kind a request without a task was recorded for.</summary>
    public static UsagePurpose PurposeOf(string kind) => kind switch
    {
        "answer" => UsagePurpose.Answer,
        "title" => UsagePurpose.Title,
        "summary" => UsagePurpose.Summary,
        "naming" => UsagePurpose.Naming,
        Conversations.RequestAttribution.WorkspaceNames => UsagePurpose.WorkspaceName,
        Conversations.RequestAttribution.RecommendQuestions or Conversations.RequestAttribution.RecommendNodes => UsagePurpose.Recommendation,
        _ => UsagePurpose.Other,
    };
}
