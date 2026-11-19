using Mindweave.Application.Statistics;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Storage;

/// <summary>
/// One local day's requests of one model, address and kind: tokens as reported and how many requests reported none.
/// <see cref="Kind"/> is the task's kind or the kind a request without a task was recorded for (<c>workspace-name</c>).
/// </summary>
public sealed record UsageRow(string Model, string BaseUrl, string Kind, DateOnly Day, TokenCounts Counts);

/// <summary>The rows of a window, and how many requests the scope has at all (any time).</summary>
public sealed record UsageRows(IReadOnlyList<UsageRow> Rows, int RequestsEver);

/// <summary>
/// What the activity facts are counted for: a workspace (null: all of them), the zone local days are counted in, where
/// the recent window starts, and the milestone thresholds.
/// </summary>
public sealed record ActivityQuery(
    WorkspaceId? Workspace,
    TimeZoneInfo Zone,
    DateTimeOffset RecentFrom,
    IReadOnlyList<int> QuestionThresholds,
    IReadOnlyList<int> SummaryThresholds);

/// <summary>
/// How a window's answers used tools (requirements §10.4.4, architecture §15.11): answers that were runs, the model requests
/// those runs made, and each tool's calls.
/// </summary>
public sealed record ToolUsage(int Runs, int ModelRequests, IReadOnlyList<ToolCallCount> Calls)
{
    public static ToolUsage None { get; } = new(0, 0, []);

    /// <summary>Model requests per run, on average; 0 without runs.</summary>
    public double RequestsPerRun => Runs == 0 ? 0 : (double)ModelRequests / Runs;
}

/// <summary>
/// One tool's calls in a window, by its name; for a third-party tool also its server's name (phase 4, decision M18: the
/// steps' <c>target</c>), so the same tool of two servers counts apart.
/// </summary>
public sealed record ToolCallCount(string Tool, int Calls, string? Server = null);

/// <summary>
/// Aggregate reads for the statistics page (2026-11-12): sums and counts computed by the database, never whole tables
/// loaded into memory. Deleted content is simply not there, so it is not counted.
/// </summary>
public interface IStatisticsStore
{
    /// <summary>
    /// Requests started in [<paramref name="from"/>, <paramref name="until"/>) for the workspace (all when null), summed per
    /// local day in <paramref name="zone"/>, model, service address and kind. A request belongs to a workspace through its
    /// task's conversation, or directly when it was recorded without a task.
    /// </summary>
    Task<UsageRows> UsageByDayAsync(WorkspaceId? workspace, DateTimeOffset from, DateTimeOffset until, TimeZoneInfo zone, CancellationToken cancellationToken);

    Task<ActivityFacts> ActivityAsync(ActivityQuery query, CancellationToken cancellationToken);

    /// <summary>Runs whose answers were asked in [<paramref name="from"/>, <paramref name="until"/>), their requests and tool calls.</summary>
    Task<ToolUsage> ToolUsageAsync(WorkspaceId? workspace, DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken);
}
