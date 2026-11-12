using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Mindweave.Application.Statistics;
using Mindweave.Application.Storage;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Sqlite;

/// <summary>
/// The statistics page's aggregates (2026-11-12). Every figure is a SUM, COUNT or a numbered pick computed by SQLite in
/// one read snapshot; only per-day rows and single numbers come back. Local days are worked out in the query from the
/// zone's offsets over the range read (<see cref="LocalCalendar.Offsets"/>), since SQLite only knows the machine's zone.
/// </summary>
internal sealed class SqliteStatisticsStore(SqliteDatabase database) : IStatisticsStore
{
    private const long DayMs = 86_400_000;

    public Task<UsageRows> UsageByDayAsync(WorkspaceId? workspace, DateTimeOffset from, DateTimeOffset until, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return database.ReadAsync(
            connection =>
            {
                var parameters = new List<(string, object?)> { ("$from", from.Ms()), ("$to", until.Ms()) };
                var day = LocalDay("r.started_at", zone, from, until, "u", parameters);
                var scope = RequestScope(workspace, parameters);
                using var command = connection.Command(null, $"""
                    SELECT coalesce(t.model, r.model), coalesce(t.base_url, r.base_url), coalesce(t.kind, r.kind), {day} AS day,
                           sum(CASE WHEN {Reported} THEN coalesce(r.prompt_tokens, 0) ELSE 0 END),
                           sum(CASE WHEN {Reported} THEN coalesce(r.completion_tokens, 0) ELSE 0 END),
                           sum(CASE WHEN {Reported} THEN 1 ELSE 0 END),
                           sum(CASE WHEN {Reported} THEN 0 ELSE 1 END)
                    FROM ai_request r
                    LEFT JOIN generation_task t ON t.id = r.task_id
                    {scope.Join}
                    WHERE r.started_at >= $from AND r.started_at < $to {scope.Where}
                    GROUP BY 1, 2, 3, 4;
                    """, [.. parameters]);
                var rows = new List<UsageRow>();
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        rows.Add(new UsageRow(
                            reader.NullableString(0) ?? string.Empty,
                            reader.NullableString(1) ?? string.Empty,
                            reader.NullableString(2) ?? string.Empty,
                            LocalCalendar.FromEpochDay(reader.GetInt64(3)),
                            new TokenCounts(reader.GetInt64(4), reader.GetInt64(5), reader.GetInt32(6), reader.GetInt32(7))));
                    }
                }

                var everParameters = new List<(string, object?)>();
                var everScope = RequestScope(workspace, everParameters);
                var ever = connection.Scalar<long>(null, $"""
                    SELECT count(*) FROM ai_request r
                    LEFT JOIN generation_task t ON t.id = r.task_id
                    {everScope.Join}
                    WHERE 1 = 1 {everScope.Where};
                    """, [.. everParameters]);
                return new UsageRows(rows, checked((int)ever));
            },
            cancellationToken);
    }

    public Task<ActivityFacts> ActivityAsync(ActivityQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return database.ReadAsync(
            connection =>
            {
                var ws = query.Workspace?.ToString();
                (string, object?)[] scope = ws is null ? [] : [("$ws", ws)];
                var inConversation = ws is null ? string.Empty : "AND c.workspace_id = $ws";
                var inNode = ws is null ? string.Empty : "AND n.workspace_id = $ws";

                int Count(string sql) => checked((int)connection.Scalar<long>(null, sql, scope));

                var questions = Count($"""
                    SELECT count(*) FROM message m JOIN conversation c ON c.id = m.conversation_id
                    WHERE m.role = 'user' {inConversation};
                    """);
                var conversations = Count($"""
                    SELECT count(*) FROM conversation c
                    WHERE NOT EXISTS (SELECT 1 FROM branch_source b WHERE b.child_id = c.id) {inConversation};
                    """);
                var branches = Count($"SELECT count(*) FROM branch_source b JOIN conversation c ON c.id = b.child_id WHERE 1 = 1 {inConversation};");
                var summaries = Count($"SELECT count(*) FROM summary_node n WHERE 1 = 1 {inNode};");
                var versions = Count($"SELECT count(*) FROM summary_version v JOIN summary_node n ON n.id = v.node_id WHERE 1 = 1 {inNode};");
                var read = Count($"""
                    SELECT count(*) FROM summary_node n JOIN summary_read_state s ON s.version_id = n.current_version_id
                    WHERE s.read_source IS NOT NULL {inNode};
                    """);
                var archived = Count($"SELECT count(*) FROM conversation_archive a JOIN conversation c ON c.id = a.conversation_id WHERE 1 = 1 {inConversation};");

                // Active days: distinct local days with a question, over the range the questions span.
                var (first, last) = QuestionRange(connection, inConversation, scope);
                var activeDays = 0;
                var recentDays = 0;
                DateOnly? firstDay = null;
                if (first is { } firstMs && last is { } lastMs)
                {
                    var parameters = new List<(string, object?)>(scope) { ("$recent", query.RecentFrom.Ms()) };
                    var day = LocalDay("m.created_at", query.Zone, DateTimeOffset.FromUnixTimeMilliseconds(firstMs), DateTimeOffset.FromUnixTimeMilliseconds(lastMs), "a", parameters);
                    using var command = connection.Command(null, $"""
                        SELECT count(DISTINCT {day}), count(DISTINCT CASE WHEN m.created_at >= $recent THEN {day} END), min({day})
                        FROM message m JOIN conversation c ON c.id = m.conversation_id
                        WHERE m.role = 'user' {inConversation};
                        """, [.. parameters]);
                    using var reader = command.ExecuteReader();
                    reader.Read();
                    activeDays = reader.GetInt32(0);
                    recentDays = reader.GetInt32(1);
                    firstDay = LocalCalendar.FromEpochDay(reader.GetInt64(2));
                }

                var milestones = new List<Milestone>();
                milestones.AddRange(Numbered(connection, MilestoneKind.Question, query.QuestionThresholds, query.Zone, scope, $"""
                    SELECT m.created_at AS at, row_number() OVER (ORDER BY m.created_at, m.rowid) AS num
                    FROM message m JOIN conversation c ON c.id = m.conversation_id
                    WHERE m.role = 'user' {inConversation}
                    """));
                milestones.AddRange(Numbered(connection, MilestoneKind.Summary, query.SummaryThresholds, query.Zone, scope, $"""
                    SELECT n.created_at AS at, row_number() OVER (ORDER BY n.created_at, n.rowid) AS num
                    FROM summary_node n WHERE 1 = 1 {inNode}
                    """));

                return new ActivityFacts(questions, conversations, branches, summaries, versions, read, archived, activeDays, recentDays, firstDay, milestones);
            },
            cancellationToken);
    }

    /// <summary>The service reported usage for the request (either count); otherwise it is counted apart, never estimated.</summary>
    private const string Reported = "(r.prompt_tokens IS NOT NULL OR r.completion_tokens IS NOT NULL)";

    /// <summary>
    /// A request belongs to the workspace through its task's conversation, or directly when it was made without a task
    /// (a workspace's suggested names). All requests when <paramref name="workspace"/> is null.
    /// </summary>
    private static (string Join, string Where) RequestScope(WorkspaceId? workspace, List<(string, object?)> parameters)
    {
        if (workspace is not { } id)
        {
            return (string.Empty, string.Empty);
        }

        parameters.Add(("$ws", id.ToString()));
        return ("LEFT JOIN conversation c ON c.id = t.conversation_id", "AND (c.workspace_id = $ws OR r.workspace_id = $ws)");
    }

    private static (long? First, long? Last) QuestionRange(SqliteConnection connection, string inConversation, (string, object?)[] scope)
    {
        using var command = connection.Command(null, $"""
            SELECT min(m.created_at), max(m.created_at) FROM message m JOIN conversation c ON c.id = m.conversation_id
            WHERE m.role = 'user' {inConversation};
            """, scope);
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.NullableInt64(0), reader.NullableInt64(1));
    }

    /// <summary>The <c>at</c> of the rows numbered as a threshold, as local days: one sort, not one query per threshold.</summary>
    private static List<Milestone> Numbered(SqliteConnection connection, MilestoneKind kind, IReadOnlyList<int> thresholds, TimeZoneInfo zone, (string, object?)[] scope, string numbered)
    {
        var list = new List<Milestone>();
        if (thresholds.Count == 0)
        {
            return list;
        }

        var parameters = new List<(string, object?)>(scope);
        var names = new List<string>();
        for (var i = 0; i < thresholds.Count; i++)
        {
            names.Add($"$k{i}");
            parameters.Add(($"$k{i}", thresholds[i]));
        }

        using var command = connection.Command(null, $"SELECT num, at FROM ({numbered}) WHERE num IN ({string.Join(", ", names)}) ORDER BY num;", [.. parameters]);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Milestone(kind, reader.GetInt32(0), LocalCalendar.DayOf(reader.Time(1), zone)));
        }

        return list;
    }

    /// <summary>
    /// SQL for the local day number (days since 1970-01-01) of a millisecond column, exact for timestamps in
    /// [<paramref name="from"/>, <paramref name="to"/>]: the column plus the zone's offset at that instant (a CASE over
    /// the offset changes in range), divided by a day. The offsets are added to <paramref name="parameters"/>.
    /// </summary>
    internal static string LocalDay(string column, TimeZoneInfo zone, DateTimeOffset from, DateTimeOffset to, string prefix, List<(string, object?)> parameters)
    {
        var changes = LocalCalendar.Offsets(zone, from, to);
        var offset = new StringBuilder();
        if (changes.Count == 1)
        {
            offset.Append(CultureInfo.InvariantCulture, $"${prefix}o0");
        }
        else
        {
            offset.Append("CASE");
            for (var i = 1; i < changes.Count; i++)
            {
                offset.Append(CultureInfo.InvariantCulture, $" WHEN {column} < ${prefix}t{i} THEN ${prefix}o{i - 1}");
            }

            offset.Append(CultureInfo.InvariantCulture, $" ELSE ${prefix}o{changes.Count - 1} END");
        }

        for (var i = 0; i < changes.Count; i++)
        {
            parameters.Add(($"${prefix}o{i}", changes[i].OffsetMs));
            if (i > 0)
            {
                parameters.Add(($"${prefix}t{i}", changes[i].FromMs));
            }
        }

        return $"(({column} + {offset}) / {DayMs})";
    }
}
