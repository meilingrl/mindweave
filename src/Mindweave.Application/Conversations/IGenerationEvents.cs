using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;

namespace Mindweave.Application.Conversations;

/// <summary>
/// How the runners raise the coordinator's events. The coordinator implements it explicitly, so every event keeps the
/// coordinator as its sender and is raised on the runner's thread, as before.
/// </summary>
internal interface IGenerationEvents
{
    void Changed();

    void AnswerEnded(AnswerOutcome outcome);

    void AnswerFinished(AnswerOutcome outcome);

    void TitleSaved(ConversationId conversation);

    void TitleAbandoned(ConversationId conversation);

    void SummaryTaskFinished(GenerationTaskRecord task);
}
