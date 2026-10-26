using Mindweave.Core.Chat;
namespace Mindweave.Application.Chat;
public enum GenerationPhase { Reply, Compressing }

public sealed record GenerationSnapshot(Guid TaskId, Guid ConversationId, Guid? AnswerMessageId,
    GenerationKind Kind, GenerationStatus Status, string Body, long Revision, string? Error, bool HasUnsavedChanges,
    GenerationPhase Phase = GenerationPhase.Reply, int CompletedContextChunks = 0, int TotalContextChunks = 0);
