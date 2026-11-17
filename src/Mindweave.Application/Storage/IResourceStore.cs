using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Workspaces;

namespace Mindweave.Application.Storage;

/// <summary>A resource as stored, with its extracted text when it has one (an image has none).</summary>
public sealed record StoredResource(Resource Resource, string? Text, bool TextTruncated = false);

public enum ResourceDeleteStatus
{
    Deleted,

    /// <summary>A message refers to it; nothing was deleted.</summary>
    Attached,

    Missing,
}

/// <summary>
/// Where an unsent draft's attachments live (architecture §15.2): a conversation's composer slot, or a workspace's 新探索 page
/// (<see cref="Conversation"/> null, <see cref="NewExplorationSlot"/>).
/// </summary>
public sealed record DraftSlot(WorkspaceId Workspace, ConversationId? Conversation, string SlotKey)
{
    public const string NewExplorationSlot = "new-exploration";

    public static DraftSlot NewExploration(WorkspaceId workspace) => new(workspace, null, NewExplorationSlot);
}

/// <summary>
/// How one answer request sent one attachment (architecture §15.4: recorded with the task's actual input): its form, and for
/// parts, which chunks (ordinals in text order) of how many. The answer's note is made from these.
/// </summary>
public sealed record ResourceInput(ResourceId Resource, string Name, Core.Context.ResourceForm Form, IReadOnlyList<int> Chunks, int ChunkCount);

/// <summary>A derivative as stored (a summary, a scaled image).</summary>
public sealed record StoredDerivative(ResourceId Resource, ResourceDerivative Derivative, DateTimeOffset CreatedAt);

/// <summary>Resource rows, derivatives, chunks and draft attachments (architecture §15.2). The originals are in <see cref="IResourceBlobStore"/>.</summary>
public interface IResourceStore
{
    /// <summary>
    /// Stores the resource, its derivatives and its text's chunks (indexed for keyword search) in one transaction, and when
    /// <paramref name="draft"/> is given, appends it to that draft. The original named by its hash must already be written.
    /// The workspace (and the conversation, when given) must exist and the conversation must be in the workspace.
    /// </summary>
    Task InsertAsync(Resource resource, IReadOnlyList<ResourceDerivative> derivatives, IReadOnlyList<ResourceChunk> chunks, DraftSlot? draft, CancellationToken cancellationToken);

    Task<StoredResource?> GetAsync(ResourceId id, CancellationToken cancellationToken);

    /// <summary>Deletes a resource no message refers to (removed from a draft, or turned back into text); its derivatives, chunks and draft row go with it.</summary>
    Task<ResourceDeleteStatus> DeleteUnattachedAsync(ResourceId id, CancellationToken cancellationToken);

    /// <summary>The draft's attachments in order.</summary>
    Task<IReadOnlyList<StoredResource>> GetDraftAsync(DraftSlot slot, CancellationToken cancellationToken);

    /// <summary>The chunks of each resource's text, in order.</summary>
    Task<IReadOnlyDictionary<ResourceId, IReadOnlyList<ResourceChunk>>> GetChunksAsync(IReadOnlyCollection<ResourceId> resources, CancellationToken cancellationToken);

    /// <summary>
    /// Keyword retrieval (architecture §15.4): for each resource, the ordinals of its chunks matching <paramref name="ftsQuery"/>
    /// (see <see cref="SearchTerms.Query"/>), best match first. Resources without a match are absent.
    /// </summary>
    Task<IReadOnlyDictionary<ResourceId, IReadOnlyList<int>>> SearchChunksAsync(IReadOnlyCollection<ResourceId> resources, string ftsQuery, CancellationToken cancellationToken);

    /// <summary>The newest derivative of <paramref name="kind"/> per resource for <paramref name="promptVersion"/> (any producer).</summary>
    Task<IReadOnlyDictionary<ResourceId, StoredDerivative>> GetDerivativesAsync(
        IReadOnlyCollection<ResourceId> resources, DerivativeKind kind, string? promptVersion, CancellationToken cancellationToken);

    /// <summary>Adds a derivative; when one with the same identity (resource, kind, producer, prompt version) exists, keeps that one.</summary>
    Task AddDerivativeAsync(ResourceId resource, ResourceDerivative derivative, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Records how the answer task sent its attachments. Diagnostic and for the answer's note.</summary>
    Task RecordTaskInputAsync(GenerationTaskId task, IReadOnlyList<ResourceInput> inputs, CancellationToken cancellationToken);

    /// <summary>For each answer of the conversation whose request sent some attachment reduced (a summary, parts, an image left out), how.</summary>
    Task<IReadOnlyDictionary<MessageId, IReadOnlyList<ResourceInput>>> ReducedInputsAsync(ConversationId conversation, CancellationToken cancellationToken);

    /// <summary>
    /// Removes unsent resources nobody needs: in no message and in no draft (startup clean-up, architecture §15.2). Returns
    /// how many were removed; their originals go with the next sweep.
    /// </summary>
    Task<int> DeleteAbandonedAsync(CancellationToken cancellationToken);
}
