using Mindweave.Application.Storage;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Conversations;

/// <summary>
/// The conversation use cases the pages use (course requirement C3: view models depend on interfaces, so a page can be
/// tested with a substitute). Implemented by <see cref="ConversationService"/>; only what pages call is here.
/// </summary>
public interface IConversations
{
    /// <inheritdoc cref="ConversationService.ModelChanged"/>
    event EventHandler? ModelChanged;

    /// <inheritdoc cref="ConversationService.ConversationChanged"/>
    event EventHandler<ConversationId>? ConversationChanged;

    /// <inheritdoc cref="ConversationService.VisibilityChanged"/>
    event EventHandler<IReadOnlyList<ConversationId>>? VisibilityChanged;

    /// <inheritdoc cref="ConversationService.ActivityChanged"/>
    event EventHandler? ActivityChanged;

    Task<IReadOnlyList<ConversationSummary>> ListAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    Task<ConversationContent?> LoadAsync(ConversationId id, CancellationToken cancellationToken);

    LiveAnswer? Observe(MessageId answer);

    /// <inheritdoc cref="ConversationService.HasUnfinishedAnswer"/>
    bool HasUnfinishedAnswer(ConversationId conversation);

    Task<ContextUsage?> GetContextUsageAsync(ConversationId id, CancellationToken cancellationToken);

    Task<SendOutcome> StartAsync(WorkspaceId workspace, string question, CancellationToken cancellationToken);

    Task<SendOutcome> AskAsync(ConversationId id, string question, CancellationToken cancellationToken);

    Task<SendOutcome> RetryAsync(ConversationId id, MessageId answerToReplace, CancellationToken cancellationToken);

    Task<BranchOutcome> CreateBranchAsync(ConversationId parent, BranchSelection selection, CancellationToken cancellationToken);

    Task<BranchSource?> GetSourceAsync(ConversationId child, CancellationToken cancellationToken);

    Task<IReadOnlyList<BranchSource>> ListBranchesAsync(ConversationId parent, CancellationToken cancellationToken);

    Task<IReadOnlyList<LineageEntry>> GetLineageAsync(ConversationId conversation, CancellationToken cancellationToken);

    Task StopAsync(MessageId answer);

    Task<ArchiveOutcome> ArchiveAsync(ConversationId id, CancellationToken cancellationToken);

    Task<RestorePlan> PlanRestoreAsync(ConversationId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConversationId>> RestoreAsync(ConversationId id, CancellationToken cancellationToken);

    Task<DeleteResult> DeleteAsync(ConversationId id, CancellationToken cancellationToken);

    Task<IReadOnlySet<ConversationId>> ArchivedAmongAsync(IReadOnlyCollection<ConversationId> conversations, CancellationToken cancellationToken);

    Task RenameAsync(ConversationId id, string title, CancellationToken cancellationToken);
}

/// <summary>What the exit flow needs from generation: what is unfinished, stopping it, saving what failed, and reopening.</summary>
public interface IGenerationExit
{
    UnfinishedUserWork UnfinishedUserWork { get; }

    Task<bool> StopForExitAsync(TimeSpan wait);

    Task<bool> SaveUnsavedAsync();

    void Reopen();
}
