using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Prompts;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Text;
using Mindweave.Core.Workspaces;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Application.Workspaces;

/// <summary>Why no names were asked for, or why asking failed.</summary>
public enum NameSuggestionProblem
{
    /// <summary>The workspace has no conversation and no summary to name it after; nothing was sent.</summary>
    EmptyWorkspace,

    /// <summary>No model is configured; nothing was sent.</summary>
    NotConfigured,

    /// <summary>Storage is paused, and with it every new AI request (architecture §12.1); nothing was sent.</summary>
    StoragePaused,

    /// <summary>New requests are refused for the moment (an exit is under way); nothing was sent.</summary>
    Unavailable,

    /// <summary>The service failed, or its output was cut off or empty; <see cref="NameSuggestions.Error"/> says how.</summary>
    Failed,

    /// <summary>Prepared in the background, it gave its slot to the user's work before it finished; it may be asked again later.</summary>
    Deferred,
}

/// <summary>Who wants the names: the user in the rename row, or the background preparing them in advance.</summary>
public enum NameRequestMode
{
    /// <summary>Asked for in the rename row: a slot at user priority.</summary>
    Asked,

    /// <summary>Prepared in the background (2026-11-11): idle priority, giving its slot up to any other work.</summary>
    Prepared,
}

/// <summary>
/// The candidate names, already cleaned and valid, none equal to the current name or to each other. Empty with no
/// <see cref="Problem"/> when the service answered but nothing in it was usable.
/// </summary>
public sealed record NameSuggestions(IReadOnlyList<WorkspaceName> Names, NameSuggestionProblem? Problem = null, GenerationErrorKind? Error = null)
{
    public static NameSuggestions Refused(NameSuggestionProblem problem, GenerationErrorKind? error = null) => new([], problem, error);
}

/// <summary>
/// Suggests names for a workspace from what it holds (2026-11-11): the model reads its conversation and summary titles
/// and proposes a few short topic names. Nothing is renamed here; the user picks one and saves it as any typed name.
/// </summary>
public interface IWorkspaceNameSuggestions
{
    /// <summary>
    /// Asks the model once. Cancelling <paramref name="cancellationToken"/> stops the request (queued or running) and
    /// throws <see cref="OperationCanceledException"/>. Never throws for a refused or failed request: the result says why.
    /// </summary>
    Task<NameSuggestions> SuggestAsync(WorkspaceId workspace, WorkspaceName current, CancellationToken cancellationToken);
}

/// <summary>
/// The request is one short call whose output is only shown: it runs through the generation coordinator (a slot under the
/// global limit of two, refused during an exit, held while storage is paused) without a task row (<see cref="TransientWork"/>).
/// Asked for in the rename row it has user priority; prepared in the background, idle priority. Either way the request is
/// recorded for the workspace (<see cref="RequestAttribution.WorkspaceNames"/>), so its token use can be counted, and the
/// names it gives are kept for the workspace (<see cref="IWorkspaceNameCache"/>) with the number of titled conversations
/// they were made from.
/// </summary>
public sealed partial class WorkspaceNameSuggester(
    IConversationStore conversations,
    ISummaryStore summaries,
    ModelSettingsService models,
    StorageHealth health,
    GenerationCoordinator generation,
    IWorkspaceNameCache cache,
    IClock clock,
    ILogger<WorkspaceNameSuggester> logger) : IWorkspaceNameSuggestions
{
    public Task<NameSuggestions> SuggestAsync(WorkspaceId workspace, WorkspaceName current, CancellationToken cancellationToken) =>
        SuggestAsync(workspace, current, NameRequestMode.Asked, cancellationToken);

    /// <summary>
    /// As <see cref="SuggestAsync(WorkspaceId, WorkspaceName, CancellationToken)"/>, for <paramref name="mode"/>. A prepared
    /// request that gave its slot up says <see cref="NameSuggestionProblem.Deferred"/>.
    /// </summary>
    public async Task<NameSuggestions> SuggestAsync(WorkspaceId workspace, WorkspaceName current, NameRequestMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);

        // Without a model nothing else matters; an empty workspace is normally already told by the disabled button.
        var model = await models.GetActiveSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            return NameSuggestions.Refused(NameSuggestionProblem.NotConfigured);
        }

        var listed = await conversations.ListAsync(workspace, cancellationToken).ConfigureAwait(false);
        var summarized = await summaries.ListAsync(workspace, cancellationToken).ConfigureAwait(false);
        var input = WorkspaceNamePrompt.SelectInput(listed, summarized);
        if (input.IsEmpty)
        {
            return NameSuggestions.Refused(NameSuggestionProblem.EmptyWorkspace);
        }

        if (health.IsBlocked)
        {
            return NameSuggestions.Refused(NameSuggestionProblem.StoragePaused);
        }

        string output;
        try
        {
            output = await generation.RequestTransientAsync(
                WorkspaceNamePrompt.Request(model, current, input),
                cancellationToken,
                mode == NameRequestMode.Asked ? GenerationPriority.User : GenerationPriority.Idle,
                new RequestAttribution(workspace, RequestAttribution.WorkspaceNames)).ConfigureAwait(false);
        }
        catch (GenerationPreemptedException)
        {
            return NameSuggestions.Refused(NameSuggestionProblem.Deferred);
        }
        catch (InvalidOperationException)
        {
            return NameSuggestions.Refused(NameSuggestionProblem.Unavailable);
        }
        catch (StorageAdmissionRefusedException)
        {
            return NameSuggestions.Refused(NameSuggestionProblem.StoragePaused);
        }
        catch (ChatClientException ex)
        {
            LogFailed(logger, ex.Kind);
            return NameSuggestions.Refused(NameSuggestionProblem.Failed, ex.Kind);
        }

        var names = WorkspaceNamePrompt.ParseCandidates(output, current);
        if (names.Count > 0)
        {
            await cache.SetAsync(workspace, new PreparedNames(names, clock.UtcNow, NameRefreshRule.TitledConversations(listed)), CancellationToken.None).ConfigureAwait(false);
        }

        return new NameSuggestions(names);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Workspace name suggestion failed: {Kind}")]
    private static partial void LogFailed(ILogger logger, GenerationErrorKind kind);
}

/// <summary>What is sent to name a workspace: its most telling titles, bounded so the request stays small.</summary>
public sealed record WorkspaceNameInput(IReadOnlyList<string> Conversations, int ConversationCount, IReadOnlyList<string> Summaries, int SummaryCount)
{
    public bool IsEmpty => Conversations.Count == 0 && Summaries.Count == 0;
}

/// <summary>The prompt, its bounded input and the cleaning of what comes back (pure; tested directly).</summary>
public static partial class WorkspaceNamePrompt
{
    /// <summary>At most this many conversation titles are sent, together at most <see cref="ConversationChars"/> characters.</summary>
    public const int MaxConversations = 20;

    public const int ConversationChars = 800;

    /// <summary>At most this many summary titles are sent, together at most <see cref="SummaryChars"/> characters.</summary>
    public const int MaxSummaries = 12;

    public const int SummaryChars = 400;

    /// <summary>Each title is cut to this many characters (a long first question stands in for an untitled conversation).</summary>
    public const int ItemLength = 30;

    /// <summary>How many names are kept.</summary>
    public const int MaxCandidates = 3;

    /// <summary>A line longer than this is an explanation, not a name, and is dropped.</summary>
    public const int MaxCandidateLength = 20;

    /// <summary>A name's limit: <see cref="MaxCandidateLength"/> characters for CJK text, 6 words for other text (the English prompt asks for 1 to 4).</summary>
    public static readonly TextLimit CandidateLimit = new(MaxCandidateLength, 6);

    /// <summary>Output cap: reasoning models count their hidden reasoning against it (see ConversationTitler).</summary>
    public const int OutputTokens = 1024;

    /// <summary>The instructions, in the interface language as the request is built.</summary>
    public static string SystemPrompt => PromptLibrary.Get("workspace-names");

    /// <summary>
    /// The titles worth sending: visible root conversations first (each starts a topic), then branches, then archived ones,
    /// the most recently active first within each; an untitled conversation by its first question. Summaries by their
    /// shown names, newest first. Repeats are sent once.
    /// </summary>
    public static WorkspaceNameInput SelectInput(IReadOnlyList<ConversationSummary> conversations, IReadOnlyList<SummaryEntry> summaries)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(summaries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The store lists the most recently active first; the stable sort keeps that order inside each group.
        var conversationTitles = conversations
            .OrderBy(c => c.IsHidden)
            .ThenBy(c => c.Parent is not null)
            .Select(c => c.Conversation.Title ?? c.FirstQuestion)
            .ToList();
        var summaryTitles = summaries
            .OrderByDescending(s => s.Current.CreatedAt)
            .Select(s => (string?)s.Node.DisplayName)
            .ToList();
        return new WorkspaceNameInput(
            Take(conversationTitles, MaxConversations, ConversationChars, seen),
            conversations.Count,
            Take(summaryTitles, MaxSummaries, SummaryChars, seen),
            summaries.Count);
    }

    public static ChatRequest Request(Core.Models.ModelSnapshot model, WorkspaceName current, WorkspaceNameInput input)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(input);
        return new ChatRequest(model with { MaxOutputTokens = OutputTokens },
        [
            new ChatTurn(ChatRole.System, SystemPrompt),
            new ChatTurn(ChatRole.User, UserText(current, input)),
        ]);
    }

    public static string UserText(WorkspaceName current, WorkspaceNameInput input)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(input);
        var text = new StringBuilder();
        text.Append(T($"Current name: {current.Value}\n", $"当前名称：{current.Value}\n"));
        if (input.Conversations.Count > 0)
        {
            text.Append(T(
                $"\nConversation titles ({input.ConversationCount} in all, {input.Conversations.Count} listed):\n",
                $"\n会话标题（共 {input.ConversationCount} 个，列出 {input.Conversations.Count} 个）：\n"));
            foreach (var title in input.Conversations)
            {
                text.Append("- ").Append(title).Append('\n');
            }
        }

        if (input.Summaries.Count > 0)
        {
            text.Append(T(
                $"\nSummary titles ({input.SummaryCount} in all, {input.Summaries.Count} listed):\n",
                $"\n总结标题（共 {input.SummaryCount} 个，列出 {input.Summaries.Count} 个）：\n"));
            foreach (var title in input.Summaries)
            {
                text.Append("- ").Append(title).Append('\n');
            }
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// The usable names in the output, in its order, at most <see cref="MaxCandidates"/>: one per line, list markers removed,
    /// cleaned as generated titles are (wrapping quotes and book-title marks only when they enclose the whole name), valid
    /// as a workspace name, short, and neither the current name nor a repeat. Headings ("候选名称：") are skipped.
    /// </summary>
    public static IReadOnlyList<WorkspaceName> ParseCandidates(string output, WorkspaceName current)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(current);
        var names = new List<WorkspaceName>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { current.Value };
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.EndsWith(':') || line.EndsWith('：') || Preamble().IsMatch(line))
            {
                continue;
            }

            // The marker may sit inside emphasis ("**1. 名称**"): strip it before and after the wrapping marks go.
            var cleaned = ConversationRules.CleanGeneratedTitle(ListMarker().Replace(line, string.Empty));
            cleaned = cleaned is null ? null : ConversationRules.CleanGeneratedTitle(ListMarker().Replace(cleaned, string.Empty));
            if (cleaned is null
                || !CandidateLimit.Fits(cleaned)
                || !WorkspaceName.TryCreate(cleaned, out var name, out _)
                || !seen.Add(name!.Value))
            {
                continue;
            }

            names.Add(name);
            if (names.Count == MaxCandidates)
            {
                break;
            }
        }

        return names;
    }

    private static List<string> Take(IEnumerable<string?> titles, int count, int chars, HashSet<string> seen)
    {
        var taken = new List<string>();
        var used = 0;
        foreach (var title in titles)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var item = ConversationRules.Shorten(title, ItemLength);
            if (seen.Contains(item))
            {
                continue;
            }

            if (taken.Count == count || used + item.Length > chars)
            {
                break;
            }

            seen.Add(item);
            taken.Add(item);
            used += item.Length;
        }

        return taken;
    }

    /// <summary>"1. ", "2、", "(3)", "- ", "• " before a name; a bare "*" or "-" stays (it may be emphasis around the name).</summary>
    [GeneratedRegex(@"^(?:[-*+]\s+|[•·]\s*|\d{1,2}\s*[.、)）:：]\s*|[（(]\d{1,2}[)）]\s*)")]
    private static partial Regex ListMarker();

    /// <summary>A chatty model's lead-in ("以下是候选名称", "Here are three names"), even without a closing colon.</summary>
    [GeneratedRegex(@"^(?:以下|下面|这里|好的|当然|候选名称|Here\b|Sure\b|Candidates?\b|Suggestions?\b)", RegexOptions.IgnoreCase)]
    private static partial Regex Preamble();
}
