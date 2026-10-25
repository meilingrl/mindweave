namespace Mindweave.Core;

public sealed record ReadingPosition(string? BlockId, double Offset, int? CharacterIndex = null);
public sealed record SourceAnchor(string ConversationId, string MessageId, string BlockId, int DisplayStart, int DisplayEnd, int RawStart, int RawEnd, string Quote);
public sealed record NavigationRequest(Conversation Conversation, ReadingPosition Position, SourceAnchor? Highlight = null);
public enum ReplyStatus { Complete, Generating, Cancelled, Interrupted, Failed }
public sealed class Message(string id, bool isUser, string markdown, ReplyStatus status = ReplyStatus.Complete)
{
    public string Id { get; } = id;
    public bool IsUser { get; } = isUser;
    public string Markdown { get; set; } = markdown;
    public ReplyStatus Status { get; set; } = status;
}

public sealed class Conversation(string id, string title, SourceAnchor? source = null)
{
    public string Id { get; } = id;
    public string Title { get; set; } = title;
    public SourceAnchor? Source { get; } = source;
    public List<Message> Messages { get; } = [];
    public ReadingPosition Position { get; set; } = new(null, 0);
    public string Draft { get; set; } = "";
}

public sealed class ExplorationState(IEnumerable<Conversation> conversations, string? currentId = null)
{
    public List<Conversation> Conversations { get; } = conversations.ToList();
    public Conversation Current { get; private set; } = currentId == null ? conversations.First() : conversations.Single(x => x.Id == currentId);
    public NavigationRequest SwitchTo(string id, ReadingPosition leavingPosition, string draft)
    {
        var target = Conversations.Single(x => x.Id == id);
        SaveCurrent(leavingPosition, draft);
        Current = target;
        return new(target, target.Position);
    }

    public NavigationRequest Branch(string messageId, string blockId, int start, int end, ReadingPosition leavingPosition, string draft)
    {
        var message = Current.Messages.Single(x => x.Id == messageId && !x.IsUser);
        if (message.Status == ReplyStatus.Generating) throw new InvalidOperationException("正在输出的回答完成或停止后，才可创建分支。");
        var block = MarkdownDocument.Parse(message.Markdown).Single(x => x.Id == blockId);
        var anchor = MarkdownDocument.Anchor(Current.Id, messageId, block, start, end);
        SaveCurrent(leavingPosition, draft);
        var title = anchor.Quote.Length > 18 ? anchor.Quote[..18] + "…" : anchor.Quote;
        var child = new Conversation(Guid.NewGuid().ToString("N"), title, anchor);
        Conversations.Add(child);
        Current = child;
        return new(child, child.Position);
    }

    public NavigationRequest ReturnToSource(ReadingPosition leavingPosition, string draft)
    {
        var source = Current.Source ?? throw new InvalidOperationException("根会话没有来源。");
        var target = Conversations.Single(x => x.Id == source.ConversationId);
        SaveCurrent(leavingPosition, draft);
        Current = target;
        return new(target, new(source.MessageId + "/" + source.BlockId, 0), source);
    }

    private void SaveCurrent(ReadingPosition position, string draft)
    {
        Current.Position = position;
        Current.Draft = draft;
    }
}
