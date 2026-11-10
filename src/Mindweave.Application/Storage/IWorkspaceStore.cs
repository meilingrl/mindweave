using Mindweave.Core.Conversations;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Storage;

public interface IWorkspaceStore
{
    /// <summary>All workspaces, oldest first.</summary>
    Task<IReadOnlyList<Workspace>> ListAsync(CancellationToken cancellationToken);

    Task AddAsync(Workspace workspace, CancellationToken cancellationToken);

    /// <summary>Stores a new name; the workspace as now stored, or null when it does not exist.</summary>
    Task<Workspace?> RenameAsync(WorkspaceId workspace, WorkspaceName name, CancellationToken cancellationToken);

    /// <summary>What deleting the workspace would remove, and which of its conversations have work running or queued.</summary>
    Task<WorkspaceContents> InspectAsync(WorkspaceId workspace, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the workspace with everything in it, in one transaction that first checks, inside it, that no answer or
    /// summary of it is queued or running (S7 round 4; a deliberate departure from requirements §5.1, chosen by the
    /// user). Conversations of every kind (branches, archived), their messages, tasks and request traces, branch
    /// sources and ranges, reading positions, drafts, context summaries, summary nodes with their versions, read
    /// states and edit drafts, node layout, the last page and the last-workspace marker go with it. Nothing changes
    /// when it is refused.
    /// </summary>
    Task<WorkspaceDeleteResult> DeleteAsync(WorkspaceId workspace, CancellationToken cancellationToken);
}

/// <summary>A conversation whose answer, or summary, is queued or being generated.</summary>
public sealed record BusyConversation(ConversationId Conversation, bool Answer, bool Summary);

/// <summary>
/// What a workspace holds, as the delete confirmation states it: every conversation (branches and archived ones
/// included) and every summary node. <paramref name="Exists"/> is false when the workspace is gone.
/// </summary>
public sealed record WorkspaceContents(bool Exists, int Conversations, int Summaries, IReadOnlyList<BusyConversation> Busy);

/// <summary>
/// Outcome of a workspace delete: <paramref name="Missing"/> when it no longer existed, refused when
/// <paramref name="Busy"/> names conversations with work running, otherwise <paramref name="Deleted"/> lists the
/// conversations that went and <paramref name="Next"/> is the most recently used workspace that remains, if any.
/// </summary>
public sealed record WorkspaceDeleteResult(bool Missing, IReadOnlyList<BusyConversation> Busy, IReadOnlyList<ConversationId> Deleted, WorkspaceId? Next)
{
    public bool IsDeleted => !Missing && Busy.Count == 0;
}
