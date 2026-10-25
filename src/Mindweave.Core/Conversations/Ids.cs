namespace Mindweave.Core.Conversations;

// Stable identities: time-ordered UUIDv7 stored as lowercase text. Separate types so they cannot be mixed up.

public readonly record struct ConversationId(Guid Value)
{
    public static ConversationId New() => new(Guid.CreateVersion7());

    public static ConversationId Parse(string text) => new(Guid.ParseExact(text, "D"));

    public override string ToString() => Value.ToString("D");
}

public readonly record struct MessageId(Guid Value)
{
    public static MessageId New() => new(Guid.CreateVersion7());

    public static MessageId Parse(string text) => new(Guid.ParseExact(text, "D"));

    public override string ToString() => Value.ToString("D");
}

public readonly record struct GenerationTaskId(Guid Value)
{
    public static GenerationTaskId New() => new(Guid.CreateVersion7());

    public static GenerationTaskId Parse(string text) => new(Guid.ParseExact(text, "D"));

    public override string ToString() => Value.ToString("D");
}
