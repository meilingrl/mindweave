using Mindweave.Application.Storage;
using Mindweave.Application.Tools;
using Mindweave.Core.Resources;
using static Mindweave.Application.Languages.Bilingual;

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
                    Shorten(T($"{name} (image {i + 1})", $"{name}（图片 {i + 1}）")), output.SourceUrl, output.Provenance, at);
                await resources.InsertAsync(kept, [], [], null, cancellationToken).ConfigureAwait(false);
                scope.Add(kept.Id);
                foreach (var placeholder in ToolOutput.ImagePlaceholders(i + 1))
                {
                    text = text.Replace(placeholder, ToolOutput.ImageMarker(kept.Id), StringComparison.Ordinal);
                }
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

    /// <summary>Most entries in the index of earlier material (L10).</summary>
    public const int MaxEarlier = 10;

    /// <summary>Most estimated tokens of that index; beyond it only the most recent entries are listed.</summary>
    public const int EarlierTokens = 300;

    /// <summary>
    /// The index of material earlier runs of the conversation's frozen chain found (docs/design/agent-loop.md L10, architecture
    /// §15.4): the most recent tool results in scope, each with its name, where it came from and its resource number, so a
    /// follow-up reads what was found instead of searching again. Null when there is none.
    /// </summary>
    public async Task<string?> EarlierMaterialAsync(ToolScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var found = new List<Resource>();
        foreach (var id in scope.Resources)
        {
            if (await resources.GetAsync(id, cancellationToken).ConfigureAwait(false) is { Resource: { Origin: ResourceOrigin.Tool or ResourceOrigin.Web or ResourceOrigin.Mcp, IsImage: false } resource })
            {
                found.Add(resource);
            }
        }

        if (found.Count == 0)
        {
            return null;
        }

        var lines = new List<string>();
        var tokens = 0;
        foreach (var resource in found.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id.ToString(), StringComparer.Ordinal).Take(MaxEarlier))
        {
            var name = resource.Name ?? SearchAttachmentsTool.ToolResult;
            var line = resource.SourceUrl is { } url
                ? T($"- Resource {resource.Id}: web page \"{name}\" ({url.Host})", $"- 资源 {resource.Id}：网页《{name}》（{url.Host}）")
                : T($"- Resource {resource.Id}: {name}", $"- 资源 {resource.Id}：{name}");
            tokens += Core.Context.TokenEstimator.Estimate(line);
            if (tokens > EarlierTokens && lines.Count > 0)
            {
                break;
            }

            lines.Add(line);
        }

        return T(
            "Material found earlier in this conversation (most recent first). When needed, search it with search_attachments, or read it by giving resource and part; there is no need to search the web again.\n",
            "本会话较早取得的材料（最近的在前）：需要时用 search_attachments 检索，或给出 resource 与 part 读取，不必重新搜索网络。\n") + string.Join('\n', lines);
    }

    private static string Shorten(string name) => name.Length > MaxName ? name[..MaxName] : name;
}
