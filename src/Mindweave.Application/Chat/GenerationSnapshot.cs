using Mindweave.Core.Chat;
namespace Mindweave.Application.Chat;
public sealed record GenerationSnapshot(Guid TaskId, Guid ConversationId, Guid? AnswerMessageId,
    GenerationKind Kind, GenerationStatus Status, string Body, long Revision, string? Error, bool HasUnsavedChanges);
