using Mindweave.Application.Exchange;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;

namespace Mindweave.Application.Import;

/// <summary>What the store has of a conversation that is also in the file.</summary>
/// <param name="HasSource">It is a branch that left from a message.</param>
/// <param name="Messages">Its messages by their numbers, whatever the file holds.</param>
public sealed record StoredConversation(ConversationId? Parent, bool IsReceived, bool HasSource, bool HasSummary, IReadOnlyDictionary<int, MessageId> Messages);

/// <summary>
/// What the store has of the file's contents, by identity: what its messages and summary versions are compared with
/// (docs/design/sharing-import.md §2). No text is in it.
/// </summary>
/// <param name="Messages">The conversation of each of the file's messages that is stored anywhere.</param>
/// <param name="Versions">The conversation of each of the file's summary versions that is stored anywhere.</param>
/// <param name="Linked">The pairs of the file's conversations whose summaries are linked, as <see cref="Pair"/> orders them.</param>
public sealed record StoredContents(
    IReadOnlyDictionary<ConversationId, StoredConversation> Conversations,
    IReadOnlyDictionary<MessageId, ConversationId> Messages,
    IReadOnlyDictionary<SummaryVersionId, ConversationId> Versions,
    IReadOnlySet<(ConversationId, ConversationId)> Linked)
{
    /// <summary>Nothing of the file is here.</summary>
    public static StoredContents Nothing { get; } = new(
        new Dictionary<ConversationId, StoredConversation>(),
        new Dictionary<MessageId, ConversationId>(),
        new Dictionary<SummaryVersionId, ConversationId>(),
        new HashSet<(ConversationId, ConversationId)>());

    /// <summary>A link has no direction.</summary>
    public static (ConversationId, ConversationId) Pair(ConversationId a, ConversationId b) => a.Value.CompareTo(b.Value) <= 0 ? (a, b) : (b, a);
}

/// <summary>A conversation that is here and gains something.</summary>
/// <param name="Messages">The messages it lacks, in order.</param>
/// <param name="Source">The message it left from, which no earlier file had.</param>
/// <param name="Summary">A summary version it lacks.</param>
public sealed record ImportGain(ExchangeConversation Conversation, IReadOnlyList<ExchangeMessage> Messages, bool Source, bool Summary);
