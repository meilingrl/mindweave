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

    /// <summary>
    /// The same, for a question sent after it was filled in from a recommendation (edited or not): the conversation
    /// remembers it, so its first summary is linked to the recommendation's basis summary (requirements §10.3.5).
    /// </summary>
    Task<SendOutcome> StartAsync(WorkspaceId workspace, string question, Recommendations.StartingRecommendation? from, CancellationToken cancellationToken);

    Task<SendOutcome> AskAsync(ConversationId id, string question, CancellationToken cancellationToken);

    /// <summary>
    /// Asks with attachments of the composer's draft (requirements §10.4.3): they leave the draft and become part of the
    /// question in the same transaction. The text may be empty when there are attachments.
    /// </summary>
    Task<SendOutcome> AskAsync(ConversationId id, string question, IReadOnlyList<Core.Resources.ResourceId> attachments, CancellationToken cancellationToken);

    /// <summary>Starts a conversation whose first question carries attachments of the 新探索 draft.</summary>
    Task<SendOutcome> StartAsync(
        WorkspaceId workspace, string question, IReadOnlyList<Core.Resources.ResourceId> attachments, Recommendations.StartingRecommendation? from, CancellationToken cancellationToken);

    Task<SendOutcome> RetryAsync(ConversationId id, MessageId answerToReplace, CancellationToken cancellationToken);

    /// <summary>Asks with the composer's choices for this question (requirements §10.4.5: the web search switch).</summary>
    Task<SendOutcome> AskAsync(ConversationId id, string question, IReadOnlyList<Core.Resources.ResourceId> attachments, SendOptions? options, CancellationToken cancellationToken) =>
        AskAsync(id, question, attachments, cancellationToken);

    /// <summary>Starts a conversation with the composer's choices for its first question.</summary>
    Task<SendOutcome> StartAsync(
        WorkspaceId workspace, string question, IReadOnlyList<Core.Resources.ResourceId> attachments, Recommendations.StartingRecommendation? from, SendOptions? options, CancellationToken cancellationToken) =>
        StartAsync(workspace, question, attachments, from, cancellationToken);

    /// <summary>Retries with the composer's choices at that moment (decision W3).</summary>
    Task<SendOutcome> RetryAsync(ConversationId id, MessageId answerToReplace, SendOptions? options, CancellationToken cancellationToken) =>
        RetryAsync(id, answerToReplace, cancellationToken);

    /// <summary>Allows or refuses the call an answer's approval card waits for (Q10). False when it no longer waits.</summary>
    bool DecideApproval(MessageId answer, string callId, bool allow) => false;

    Task<BranchOutcome> CreateBranchAsync(ConversationId parent, BranchSelection selection, CancellationToken cancellationToken);

    Task<BranchSource?> GetSourceAsync(ConversationId child, CancellationToken cancellationToken);

    Task<IReadOnlyList<BranchSource>> ListBranchesAsync(ConversationId parent, CancellationToken cancellationToken);

    Task<IReadOnlyList<LineageEntry>> GetLineageAsync(ConversationId conversation, CancellationToken cancellationToken);

    Task StopAsync(MessageId answer);

    Task<ArchiveOutcome> ArchiveAsync(ConversationId id, CancellationToken cancellationToken);

    Task<RestorePlan> PlanRestoreAsync(ConversationId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConversationId>> RestoreAsync(ConversationId id, CancellationToken cancellationToken);

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

/// <summary>The composer's choices that go with a question: the web search switch (requirements §10.4.5).</summary>
public sealed record SendOptions(Core.Runs.WebSearchMode WebSearch);
