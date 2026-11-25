using System.Text.Json;
using Mindweave.Application.Exchange;
using Mindweave.Application.Tools;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Share;

/// <summary>
/// Which of a conversation's content leaves in a share with content (docs/design/sharing.md S6, S7, §6.6, §9). The exchange
/// document is built with it, and nothing else decides.
/// </summary>
internal sealed class ShareContentFilter(ShareContents contents, IReadOnlySet<ConversationId> ticked)
{
    /// <summary>The characters a formula or a task-list box shows as in a quote.</summary>
    private const char Placeholder = '\uFFFC';

    private readonly HashSet<MessageId> _leftFrom = [.. ticked
        .Select(id => contents.Conversations.GetValueOrDefault(id)?.Source?.SourceMessage)
        .OfType<MessageId>()];

    private readonly Dictionary<SummaryNodeId, (ConversationId Conversation, string Title)> _summaries = contents.Conversations
        .Where(c => ticked.Contains(c.Key) && c.Value is { SummaryNode: not null, Summary: not null })
        .ToDictionary(c => c.Value.SummaryNode!.Value, c => (c.Key, c.Value.Summary!.Title));

    public ExchangeContent Of(ConversationId id)
    {
        var content = contents.Conversations[id];
        return new ExchangeContent(
            [.. Messages(content.Messages)],
            content.Source is { } source ? Source(source, content.SourceOffset) : null,
            content.Summary is { } summary ? new ExchangeSummary(summary.Id, summary.Title, summary.Body, summary.CreatedAt.ToUniversalTime()) : null);
    }

    /// <summary>The links between two shared conversations, with their notes.</summary>
    public IEnumerable<ExchangeLink> Links() => contents.Links
        .Where(l => _summaries.ContainsKey(l.NodeA) && _summaries.ContainsKey(l.NodeB))
        .Select(l => new ExchangeLink(_summaries[l.NodeA].Conversation, _summaries[l.NodeB].Conversation) { Note = l.Note });

    /// <summary>
    /// Each question with the answer that stands: its latest attempt that ended with any text. While an answer is being
    /// generated the share goes up to the last finished one, so a question not answered yet is left out (§9).
    /// </summary>
    private IEnumerable<ExchangeMessage> Messages(IReadOnlyList<Message> ordered)
    {
        var end = ConversationRules.HasUnfinishedAnswer(ordered)
            ? ordered.LastOrDefault(m => m.Role == MessageRole.Assistant && m.IsFinished)?.Seq ?? -1
            : int.MaxValue;
        var attempts = new List<Message>();
        foreach (var message in ordered.Where(m => m.Seq <= end))
        {
            if (message.Role == MessageRole.Assistant)
            {
                attempts.Add(message);
                continue;
            }

            foreach (var answer in Answers(attempts))
            {
                yield return answer;
            }

            attempts.Clear();
            yield return Question(message);
        }

        foreach (var answer in Answers(attempts))
        {
            yield return answer;
        }
    }

    /// <summary>The answer that stands, after any earlier attempt a shared branch left from (§6.6, point 4).</summary>
    private IEnumerable<ExchangeMessage> Answers(IReadOnlyList<Message> attempts)
    {
        var withText = attempts.Where(a => a.IsFinished && !string.IsNullOrWhiteSpace(a.Text)).ToList();
        return withText.SkipLast(1).Where(a => _leftFrom.Contains(a.Id)).Select(a => Answer(a) with { ReplacedBy = withText[^1].Id })
            .Concat(withText.TakeLast(1).Select(Answer));
    }

    private static ExchangeMessage Question(Message message) =>
        new(message.Id, message.Role, message.CreatedAt.ToUniversalTime(), message.State, message.Text)
        {
            Attachments = [.. ContentBlocks.AttachmentsOf(message.Blocks).Select(a => string.IsNullOrWhiteSpace(a.Name) ? null : a.Name)],
        };

    private ExchangeMessage Answer(Message message) =>
        new(message.Id, message.Role, (message.CompletedAt ?? message.CreatedAt).ToUniversalTime(), message.State, message.Text)
        {
            Activity = Activity([.. message.Blocks.OfType<ToolCallBlock>().Select(c => c.Tool)]),
            Citations = [.. ContentBlocks.CitationsOf(message.Blocks).Select(Citation)],
        };

    private static ExchangeActivity? Activity(IReadOnlyList<string> tools)
    {
        if (tools.Count == 0)
        {
            return null;
        }

        int Of(string tool) => tools.Count(t => t == tool);
        var (attachments, summaries, searches, pages) = (Of(SearchAttachmentsTool.ToolName), Of(SearchSummariesTool.ToolName), Of(WebSearchTool.ToolName), Of(FetchPageTool.ToolName));
        return new ExchangeActivity(attachments, summaries, searches, pages, tools.Count - attachments - summaries - searches - pages);
    }

    private ExchangeCitation Citation(CitationBlock citation) => citation.Kind switch
    {
        CitationKind.Web when citation.Url is { } url && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) =>
            new ExchangeCitation(citation.Number, citation.Kind, citation.Title, url),
        CitationKind.Summary when Guid.TryParseExact(citation.Target, "D", out var node) && _summaries.TryGetValue(new SummaryNodeId(node), out var shared) =>
            new ExchangeCitation(citation.Number, citation.Kind, shared.Title, Summary: shared.Conversation),
        _ => new ExchangeCitation(citation.Number, citation.Kind),
    };

    private static ExchangeBranchSource Source(BranchSource source, int? offset) => new(
        source.SourceMessage,
        source.Quote,
        offset is { } at ? new ExchangePosition(source.SourceBlock, at) : null,
        HoldsOnly(source.AnchorJson, source.Quote) ? new ExchangeAnchor(source.AnchorFormatVersion, source.RenderProfile, source.AnchorJson) : null);

    /// <summary>
    /// Whether the anchor holds no text beyond the quote. It is opaque here, so every string in it is looked at: it must be
    /// part of the quote or a hash, and no name may be more than a word.
    /// </summary>
    private static bool HoldsOnly(string anchor, string quote)
    {
        try
        {
            using var parsed = JsonDocument.Parse(anchor);
            return Holds(parsed.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }

        bool Holds(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().All(p => p.Name.All(char.IsAsciiLetterOrDigit) && Holds(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().All(Holds),
            JsonValueKind.String => Allowed(element.GetString()!),
            _ => true,
        };

        bool Allowed(string text) =>
            (text.Length == 64 && text.All(char.IsAsciiHexDigit))
            || text.Split(Placeholder, StringSplitOptions.TrimEntries).All(part => quote.Contains(part, StringComparison.Ordinal));
    }
}
