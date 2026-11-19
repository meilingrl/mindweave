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
    public async Task<(ResourceId Id, string Text)> StoreResultAsync(ToolScope scope, string name, ToolOutput output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(output);
        health.ThrowIfBlocked();
        var text = output.Text;
        name = output.Name ?? name;
        try
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(clock.UtcNow.ToUnixTimeMilliseconds());
            for (var i = 0; i < output.Images.Count; i++)
            {
                // Each image is its own resource (phase 4, decision M15); its marker in the text becomes its ID.
                var image = output.Images[i];
                using var picture = await blobs.WriteAsync(image.Data.ToArray(), cancellationToken).ConfigureAwait(false);
                var kept = new Resource(
                    ResourceId.New(), scope.Workspace, scope.Conversation, picture.Hash, image.MediaType, picture.Size, output.Origin,
                    Shorten($"{name}（图片 {i + 1}）"), output.SourceUrl, output.Provenance, at);
                await resources.InsertAsync(kept, [], [], null, cancellationToken).ConfigureAwait(false);
                scope.Add(kept.Id);
                text = text.Replace($"[图片 {i + 1}]", $"[图片：资源 {kept.Id}]", StringComparison.Ordinal);
            }

            var derived = new ResourceDerivative(DerivativeKind.Text, text, "tool/1", EstimatedTokens: Core.Context.TokenEstimator.Estimate(text));
            using var blob = await blobs.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
            var resource = new Resource(
                ResourceId.New(), scope.Workspace, scope.Conversation, blob.Hash, MediaTypes.Markdown, blob.Size, output.Origin,
                Shorten(name), output.SourceUrl, output.Provenance, at);
            await resources.InsertAsync(resource, [derived], ResourceChunker.Split(text), null, cancellationToken).ConfigureAwait(false);
            scope.Add(resource.Id);
            output.Stored?.Invoke(resource.Id);
            return (resource.Id, text);
        }
        catch (StorageWriteException ex)
        {
            health.Report(ex);
            throw;
        }
    }

    private static string Shorten(string name) => name.Length > MaxName ? name[..MaxName] : name;
}
