namespace Mindweave.Core.Chat;

public sealed record ChatContext(IReadOnlyList<ChatMessage> Messages, string? SourceQuote, Guid? DirectSourceMessageId = null);
