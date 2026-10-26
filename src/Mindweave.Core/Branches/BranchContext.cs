using Mindweave.Core.Conversations;

namespace Mindweave.Core.Branches;

/// <summary>
/// One conversation's share of a branch's context: its messages (already cut to the frozen range for an
/// ancestor) and, if it is itself a branch, the quote it was created from.
/// </summary>
public sealed record ContextSegment(ConversationId Conversation, string? Quote, IReadOnlyList<Message> Messages);

/// <summary>A message as it is sent: the stored message and the text that goes out for it.</summary>
public sealed record ContextItem(Message Message, string Text);

/// <summary>Assembles what a conversation sends as history, across its frozen ancestor chain (§6, §4.1).</summary>
public static class BranchContext
{
    /// <summary>
    /// Ancestors from root to parent, then the conversation itself. Each segment keeps the latest usable attempt
    /// per question; an ancestor's last message (the next level's source answer) is kept even when it is a
    /// partial failed or interrupted answer, because the branch is about it. The first question of every branch
    /// segment is prefixed with its quote, so the model knows which passage the follow-up refers to.
    /// </summary>
    public static IReadOnlyList<ContextItem> Assemble(IReadOnlyList<ContextSegment> ancestors, ContextSegment own)
    {
        ArgumentNullException.ThrowIfNull(ancestors);
        ArgumentNullException.ThrowIfNull(own);
        var items = new List<ContextItem>();
        foreach (var ancestor in ancestors)
        {
            AddSegment(items, ancestor, keepSource: true);
        }

        AddSegment(items, own, keepSource: false);
        return items;
    }

    /// <summary>The text sent for a branch's first question: the quoted passage, then the question.</summary>
    public static string QuotedQuestion(string quote, string question)
    {
        ArgumentNullException.ThrowIfNull(quote);
        var quoted = string.Join('\n', quote.Split('\n').Select(line => "> " + line.TrimEnd('\r')));
        return $"关于上文中的这段内容：\n{quoted}\n\n{question}";
    }

    private static void AddSegment(List<ContextItem> items, ContextSegment segment, bool keepSource)
    {
        var selected = ConversationRules.SelectContext(segment.Messages).ToList();
        if (keepSource && segment.Messages.Count > 0 && segment.Messages[^1] is { Role: MessageRole.Assistant } source
            && !selected.Contains(source) && !string.IsNullOrWhiteSpace(source.Text))
        {
            selected.Add(source);
        }

        var firstQuestion = segment.Quote is null ? null : selected.FirstOrDefault(m => m.Role == MessageRole.User);
        foreach (var message in selected)
        {
            items.Add(new ContextItem(message, message == firstQuestion ? QuotedQuestion(segment.Quote!, message.Text) : message.Text));
        }
    }
}
