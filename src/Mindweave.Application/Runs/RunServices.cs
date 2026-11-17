using Mindweave.Application.Storage;
using Mindweave.Application.Tools;
using Mindweave.Core.Resources;

namespace Mindweave.Application.Runs;

/// <summary>What the run loop needs beyond an answer's own plumbing: its step store and where tool results are kept.</summary>
public sealed class RunServices(IRunStore store, IResourceStore resources, IResourceBlobStore blobs, StorageHealth health, IClock clock)
{
    public IRunStore Store { get; } = store;

    /// <summary>Longest name a tool result resource is given (the tool and its input).</summary>
    private const int MaxName = 80;

    /// <summary>
    /// Keeps a tool's whole result as a resource of the run's conversation (architecture §15.12: origin <c>tool</c>, text,
    /// chunked and indexed so 检索附件 finds it later) and adds it to the run's scope. The original is written first, then the
    /// row, while the hash is pinned. A storage condition is reported and thrown.
    /// </summary>
    public async Task<ResourceId> StoreResultAsync(ToolScope scope, string name, ToolOutput output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(output);
        health.ThrowIfBlocked();
        var text = output.Text;
        var derived = new ResourceDerivative(DerivativeKind.Text, text, "tool/1", EstimatedTokens: Core.Context.TokenEstimator.Estimate(text));
        try
        {
            using var blob = await blobs.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
            var at = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
            var resource = new Resource(
                ResourceId.New(), scope.Workspace, scope.Conversation, blob.Hash, MediaTypes.Markdown, blob.Size, ResourceOrigin.Tool,
                name.Length > MaxName ? name[..MaxName] : name, null, output.Provenance, at);
            await resources.InsertAsync(resource, [derived], ResourceChunker.Split(text), null, cancellationToken).ConfigureAwait(false);
            scope.Add(resource.Id);
            return resource.Id;
        }
        catch (StorageWriteException ex)
        {
            health.Report(ex);
            throw;
        }
    }
}
