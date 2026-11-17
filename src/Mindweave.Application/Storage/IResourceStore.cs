using Mindweave.Core.Resources;

namespace Mindweave.Application.Storage;

/// <summary>A resource as stored, with its extracted text when it has one (an image has none).</summary>
public sealed record StoredResource(Resource Resource, string? Text);

public enum ResourceDeleteStatus
{
    Deleted,

    /// <summary>A message refers to it; nothing was deleted.</summary>
    Attached,

    Missing,
}

/// <summary>Resource rows and their derivatives (docs/design/resources.md). The originals are in <see cref="IResourceBlobStore"/>.</summary>
public interface IResourceStore
{
    /// <summary>
    /// Stores the resource and its derivatives in one transaction. The original named by its hash must already be
    /// written. The workspace (and the conversation, when given) must exist and the conversation must be in the workspace.
    /// </summary>
    Task InsertAsync(Resource resource, IReadOnlyList<ResourceDerivative> derivatives, CancellationToken cancellationToken);

    Task<StoredResource?> GetAsync(ResourceId id, CancellationToken cancellationToken);

    /// <summary>Deletes a resource no message refers to (a draft's attachment turned back into text); its derivatives go with it.</summary>
    Task<ResourceDeleteStatus> DeleteUnattachedAsync(ResourceId id, CancellationToken cancellationToken);
}
