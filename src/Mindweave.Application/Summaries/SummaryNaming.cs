using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Summaries;

/// <summary>
/// Same-name disambiguation (§10.2): when two or more summaries of a workspace share a base title, one best-effort
/// naming request asks for a short distinguishing term for each. Groups are deduplicated while their request is
/// queued or running; the marks are dropped when it applies its result or its task finishes without one.
/// </summary>
internal sealed partial class SummaryNaming
{
    private const int NamingOutputTokens = 4_096;
    private const int NamingBodyExcerpt = 600;

    private const string NamingInstructions = """
        下面是同一工作区里基础标题相同的几条知识总结。为每一条给出一个简短的区分词（2 到 8 个字），说明它与其他几条的区别，必须能在它的正文里找到依据。
        现有名称里的区分词仍然准确时保留原词。确实无法区分时留空，不要编造区别。
        每行输出一条，格式为“编号：区分词”，不要输出其他内容。
        """;

    private static readonly Regex NamingLine = new(@"^\s*\[?(?<n>\d+)\]?\s*[：:.、]\s*(?<term>.*)$", RegexOptions.CultureInvariant);

    private readonly ISummaryStore _summaries;
    private readonly IConversationStore _conversations;
    private readonly GenerationCoordinator _generation;
    private readonly StorageHealth _health;
    private readonly ModelSettingsService _models;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly Action<ConversationId> _changed;
    private readonly ConcurrentDictionary<(WorkspaceId, string), byte> _namingQueued = new();
    private readonly ConcurrentDictionary<GenerationTaskId, (WorkspaceId, string)> _namingTasks = new();

    /// <param name="changed">Raises the owner's change notification for a conversation whose summary was renamed.</param>
    public SummaryNaming(
        ISummaryStore summaries,
        IConversationStore conversations,
        GenerationCoordinator generation,
        StorageHealth health,
        ModelSettingsService models,
        IClock clock,
        ILogger logger,
        Action<ConversationId> changed)
    {
        _summaries = summaries;
        _conversations = conversations;
        _generation = generation;
        _health = health;
        _models = models;
        _clock = clock;
        _logger = logger;
        _changed = changed;
    }

    /// <summary>A summary task finished. A naming task stopped before its output arrived (an archive, an exit) never reached Apply's cleanup.</summary>
    public void OnTaskFinished(GenerationTaskRecord task)
    {
        if (_namingTasks.TryRemove(task.Id, out var group))
        {
            _namingQueued.TryRemove(group, out byte _);
        }
    }

    /// <summary>
    /// After the current version changed (§10.2): if its title no longer matches the base title, the base title and
    /// (unless manual) the display name follow it, and the new name's same-name group is checked.
    /// </summary>
    public async Task CheckNameAsync(SummaryNodeId node, CancellationToken cancellationToken)
    {
        if (await _summaries.GetAsync(node, cancellationToken).ConfigureAwait(false) is not { } detail)
        {
            return;
        }

        if (SummaryRules.NormalizeName(detail.Current.Title) != SummaryRules.NormalizeName(detail.Node.BaseTitle))
        {
            await _summaries.UpdateBaseTitleAsync(node, detail.Node.Current, detail.Current.Title, _clock.UtcNow, cancellationToken).ConfigureAwait(false);
            await ScheduleNamingSafelyAsync(node).ConfigureAwait(false);
        }
    }

    /// <summary>Queues naming for the node's same-name group when needed. Never throws.</summary>
    public async Task ScheduleNamingSafelyAsync(SummaryNodeId node)
    {
        try
        {
            await ScheduleNamingAsync(node).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Naming is best effort; the summary is already saved under its base name.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogNamingNotScheduled(_logger, ex, node);
        }
    }

    /// <summary>"编号：区分词" lines; a missing or empty line leaves that node with its base name.</summary>
    internal static string?[] ParseNaming(string output, int count)
    {
        var terms = new string?[count];
        foreach (var line in output.Split('\n'))
        {
            var match = NamingLine.Match(line);
            if (match.Success && int.TryParse(match.Groups["n"].Value, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= count)
            {
                var term = match.Groups["term"].Value.Trim().Trim('“', '”', '"', '「', '」');
                terms[n - 1] = term.Length is > 0 and <= 16 ? term : null;
            }
        }

        return terms;
    }

    /// <summary>Queues naming for the node's same-name group when it has two or more members; groups are deduplicated.</summary>
    private async Task ScheduleNamingAsync(SummaryNodeId node)
    {
        if (await _summaries.GetAsync(node, CancellationToken.None).ConfigureAwait(false) is not { } detail)
        {
            return;
        }

        var key = SummaryRules.NormalizeName(detail.Node.BaseTitle);
        var members = (await _summaries.ListAsync(detail.Node.Workspace, CancellationToken.None).ConfigureAwait(false))
            .Where(e => SummaryRules.NormalizeName(e.Node.BaseTitle) == key)
            .ToList();
        if (members.Count < 2 || members.All(e => e.Node.NameSource == NameSource.Manual) || _health.IsBlocked)
        {
            return;
        }

        // Excerpts of exactly the versions the group was read with; a member whose version is gone meanwhile drops out.
        // One whose node has moved on keeps its group-time excerpt, and the apply-time expectation discards its name.
        var excerpts = await _summaries.ExcerptsAsync([.. members.Select(e => e.Current.Id)], NamingBodyExcerpt, CancellationToken.None).ConfigureAwait(false);
        var group = members.Where(e => excerpts.ContainsKey(e.Current.Id)).ToList();
        if (group.Count < 2)
        {
            return;
        }

        if (await _models.GetActiveSnapshotAsync(CancellationToken.None).ConfigureAwait(false) is not { } model || !_namingQueued.TryAdd((detail.Node.Workspace, key), 0))
        {
            return;
        }

        var prompt = new StringBuilder().Append("基础标题：").Append(detail.Node.BaseTitle).Append("\n\n");
        for (var i = 0; i < group.Count; i++)
        {
            prompt.Append(CultureInfo.InvariantCulture, $"[{i + 1}] 现有名称：{group[i].Node.DisplayName}\n标题：{group[i].Current.Title}\n正文开头：{excerpts[group[i].Current.Id]}\n\n");
        }

        var expectations = group.Select(e => new NamingExpectation(e.Node.Id, e.Node.Current, e.Node.DisplayName)).ToList();
        var baseTitles = group.Select(e => e.Node.BaseTitle).ToList();
        var task = new GenerationTaskRecord(
            GenerationTaskId.New(), GenerationTaskKind.Naming, detail.Node.Conversation, null, model with { MaxOutputTokens = NamingOutputTokens }, [], _clock.UtcNow);
        await _conversations.AddTaskAsync(task, CancellationToken.None).ConfigureAwait(false);
        _namingTasks[task.Id] = (detail.Node.Workspace, key);
        _generation.Enqueue(new NamingWork(
            task,
            new ChatRequest(task.Model, [new ChatTurn(ChatRole.System, NamingInstructions), new ChatTurn(ChatRole.User, prompt.ToString())]),
            async output =>
            {
                try
                {
                    var terms = ParseNaming(output, group.Count);
                    var names = expectations.Select((e, i) => (e, SummaryRules.DisplayName(baseTitles[i], terms[i]))).ToList();
                    var renamed = await _summaries.ApplyNamesAsync(names, _clock.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    foreach (var entry in group.Where(e => renamed.Contains(e.Node.Id)))
                    {
                        _changed(entry.Node.Conversation);
                    }
                }
                finally
                {
                    _namingTasks.TryRemove(task.Id, out _);
                    _namingQueued.TryRemove((detail.Node.Workspace, key), out _);
                }
            }));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Naming for summary node {Node} not scheduled")]
    private static partial void LogNamingNotScheduled(ILogger logger, Exception exception, SummaryNodeId node);
}
