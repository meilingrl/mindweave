using Mindweave.Application.Resources;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Resources;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Tests;

/// <summary>Resources for a startup that has none: the clean-up finds nothing, and nothing else is asked of it.</summary>
internal sealed class NoResources : IResources
{
    public Task CleanUpAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<ResourceCreateResult> CreateFromPasteAsync(WorkspaceId workspace, ConversationId? conversation, string text, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ResourceCreateResult> CreateFromFileAsync(WorkspaceId workspace, ConversationId? conversation, string path, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ResourceCreateResult> AttachPasteAsync(DraftSlot draft, string text, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ResourceCreateResult> AttachFileAsync(DraftSlot draft, string path, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ResourceCreateResult> AttachImageAsync(DraftSlot draft, byte[] bytes, string? name, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<IReadOnlyList<StoredResource>> GetDraftAsync(DraftSlot draft, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<StoredResource?> GetAsync(ResourceId id, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<Stream?> OpenOriginalAsync(ResourceId id, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<bool> HasOriginalAsync(Resource resource) => throw new NotSupportedException();

    public Task<ResourceDeleteStatus> DeleteUnattachedAsync(ResourceId id, CancellationToken cancellationToken) => throw new NotSupportedException();
}
