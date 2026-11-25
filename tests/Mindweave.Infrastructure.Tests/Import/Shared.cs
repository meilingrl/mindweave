using Mindweave.Application.Exchange;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;

namespace Mindweave.Infrastructure.Tests.Import;

/// <summary>Shares with content made by hand, for what the app's own export does not happen to write.</summary>
internal static class Shared
{
    public static readonly DateTimeOffset Day = new(2026, 11, 18, 11, 42, 30, 120, TimeSpan.Zero);

    public static ExchangeMessage Question(int seq, string text, params string?[] attachments) =>
        new(MessageId.New(), seq, MessageRole.User, Day.AddMinutes(seq), MessageState.Complete, text) { Attachments = attachments };

    public static ExchangeMessage Answer(int seq, string text, MessageState state = MessageState.Complete) =>
        new(MessageId.New(), seq, MessageRole.Assistant, Day.AddMinutes(seq), state, text);

    public static ExchangeSummary Summary(string title, int toSeq) => new(SummaryVersionId.New(), title, "The text of " + title, Day.AddHours(1), toSeq);

    public static ExchangeBranchSource From(ExchangeMessage answer, string quote) => new(answer.Id, quote, null, null);

    public static ExchangeConversation Conversation(ExchangeConversation? parent, string? name, ExchangeBranchSource? source, params ExchangeMessage[] messages) =>
        new(ConversationId.New(), parent?.Id, name, Day.AddMinutes(parent is null ? 0 : 30), messages.Count(m => m.Role == MessageRole.User), false)
        {
            Content = new ExchangeContent(messages, source, null),
        };

    public static ExchangeConversation With(this ExchangeConversation conversation, ExchangeSummary summary) =>
        conversation with { HasSummary = true, Content = conversation.Content! with { Summary = summary } };

    /// <summary>The same conversation with more said in it.</summary>
    public static ExchangeConversation And(this ExchangeConversation conversation, params ExchangeMessage[] more) =>
        conversation with { Content = conversation.Content! with { Messages = [.. conversation.Content.Messages.Concat(more).OrderBy(m => m.Seq)] } };

    public static ExchangeDocument Document(params ExchangeConversation[] conversations) =>
        new(ExchangeDocument.ContentKind, "How trains came to be", Day, "0.7.0", conversations, []);

    /// <summary>The structure-only share of the same conversations.</summary>
    public static ExchangeDocument Structure(this ExchangeDocument document) =>
        document with { Kind = ExchangeDocument.StructureKind, Conversations = [.. document.Conversations.Select(c => c with { Content = null })], Links = [.. document.Links.Select(l => l with { Note = null })] };
}
