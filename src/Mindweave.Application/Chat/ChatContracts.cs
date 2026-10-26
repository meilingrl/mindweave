using Mindweave.Core.Chat;

namespace Mindweave.Application.Chat;

public interface IChatStore
{
    Task<IReadOnlyList<Conversation>> ListConversationsAsync(Guid workspaceId);
    Task<Conversation> CreateConversationAsync(Guid workspaceId);
    Task<Conversation?> LoadConversationAsync(Guid conversationId);
    Task RenameConversationAsync(Guid conversationId, string title);
    Task<IReadOnlyList<ChatMessage>> ReadMessagesAsync(Guid conversationId);
    Task<ChatContext> ReadContextAsync(Guid conversationId);
    Task<AiConfiguration?> LoadActiveConfigurationAsync();
    Task SaveConfigurationAsync(AiConfiguration configuration);
    Task<GenerationWork> AcceptReplyAsync(Guid conversationId, string? userText, AiConfiguration configuration);
    Task<GenerationWork?> AcceptTitleAsync(Guid conversationId, AiConfiguration configuration);
    Task<bool> WriteGenerationAsync(Guid taskId, long revision, string body, GenerationStatus status, string? errorCode = null, string? actualModelId = null);
    Task<bool> CompleteTitleAsync(Guid taskId, string title);
    Task<GenerationRecord?> LoadGenerationAsync(Guid taskId);
    Task<IReadOnlyList<GenerationRecord>> ListActiveGenerationsAsync();
    Task RecoverInterruptedAsync();
    Task<ConversationDraft> LoadDraftAsync(Guid conversationId);
    Task SaveDraftAsync(Guid conversationId, string text, long revision);
}
public interface ICredentialVault
{
    Task<string> WriteNewAsync(string secret);
    Task<string> ReadAsync(string reference);
    Task DeleteAsync(string reference);
}
public sealed record ChatInput(string Role, string Content);
public sealed record ChatRequest(AiConfiguration Configuration, IReadOnlyList<ChatInput> Messages);
public sealed record ChatStreamEvent(string? Text = null, string? FinishReason = null, string? ActualModelId = null);
public interface IChatTransport
{
    IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string secret, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListModelsAsync(AiConfiguration configuration, string secret, CancellationToken cancellationToken);
}
