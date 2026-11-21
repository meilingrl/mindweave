using System.Text;
using Microsoft.Extensions.Logging;
using Mindweave.Application.Ai;
using Mindweave.Application.Prompts;
using Mindweave.Application.Resources;
using Mindweave.Application.Storage;
using Mindweave.Core.Branches;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Resources;
using static Mindweave.Application.Languages.Bilingual;

namespace Mindweave.Application.Conversations;

/// <summary>What a message's attachments add to its turn: material text before the question, and image parts.</summary>
public sealed record TurnMaterials(string Text, IReadOnlyList<ChatImage> Images);

/// <summary>
/// The resource layer of a request (architecture §15.4): which attachments the planned messages carry and what each can be
/// sent as (<see cref="CandidatesAsync"/>), the summaries a plan needs (<see cref="SummarizeAsync"/>, made once per
/// attachment and prompt version and reused), and the material each message's turn carries (<see cref="MaterialsAsync"/>).
/// </summary>
public sealed partial class ResourceContext(IResourceStore store, ResourceService resources, IClock clock, ILogger<ResourceContext> logger)
{
    /// <summary>Version of the attachment summary prompt: a summary is reused only for the same version.</summary>
    public const string SummaryPromptVersion = "1";

    /// <summary>Output asked for one summary request (room for reasoning models, as for context summaries).</summary>
    public const int SummaryOutputTokens = 8_192;

    /// <summary>
    /// The candidates of every attachment in <paramref name="items"/>, in order. Chunks of the current question's attachments
    /// are ranked by keyword relevance to <paramref name="question"/> (matches first, best first, then the rest in text order);
    /// an image is an image only for a model that declares <see cref="ModelCapability.ImageInput"/> and whose original is there.
    /// </summary>
    public async Task<IReadOnlyList<ResourceCandidate>> CandidatesAsync(IReadOnlyList<ContextItem> items, string question, ModelSnapshot model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(model);
        var attached = items
            .SelectMany(i => ContentBlocks.AttachmentsOf(i.Message.Blocks).Select(a => (Item: i, Attachment: a)))
            .ToList();
        if (attached.Count == 0)
        {
            return [];
        }

        var current = items[^1].Message.Id;
        var texts = attached.Where(a => a.Attachment.Text is not null).Select(a => a.Attachment.Resource).Distinct().ToList();
        var summaries = await store.GetDerivativesAsync(texts, DerivativeKind.Summary, SummaryPromptVersion, cancellationToken).ConfigureAwait(false);
        var chunks = await store.GetChunksAsync(texts, cancellationToken).ConfigureAwait(false);
        var currentTexts = attached.Where(a => a.Item.Message.Id == current && a.Attachment.Text is not null).Select(a => a.Attachment.Resource).ToList();
        var ranked = currentTexts.Count > 0 && SearchTerms.Query(question) is { } query
            ? await store.SearchChunksAsync(currentTexts, query, cancellationToken).ConfigureAwait(false)
            : new Dictionary<ResourceId, IReadOnlyList<int>>();

        var canSee = model.Capabilities.Has(ModelCapability.ImageInput);
        var result = new List<ResourceCandidate>();
        foreach (var (item, attachment) in attached)
        {
            if (attachment.Text is null)
            {
                var stored = await store.GetAsync(attachment.Resource, cancellationToken).ConfigureAwait(false);
                var kind = !canSee ? ResourceKind.ImageUnsupported
                    : stored is null || !resources.HasOriginal(stored.Resource) ? ResourceKind.ImageMissing
                    : ResourceKind.Image;
                var tokens = kind == ResourceKind.Image
                    ? ResourceMaterial.ImageTokens + TokenEstimator.Estimate(ResourceMaterial.ImageLabel(attachment))
                    : TokenEstimator.Estimate(PlaceholderOf(attachment, kind));
                result.Add(new ResourceCandidate(attachment, item.Message.Id, kind, tokens, null, []));
                continue;
            }

            var own = chunks.TryGetValue(attachment.Resource, out var c) ? c : [];
            var order = ranked.TryGetValue(attachment.Resource, out var hits) ? hits : [];
            var byOrdinal = own.ToDictionary(x => x.Ordinal);
            IReadOnlyList<ResourceChunk> rankedChunks = [.. order.Where(byOrdinal.ContainsKey).Select(o => byOrdinal[o]), .. own.Where(x => !order.Contains(x.Ordinal))];
            int? summaryTokens = summaries.TryGetValue(attachment.Resource, out var summary)
                ? TokenEstimator.Estimate(ResourceMaterial.Summary(attachment, summary.Derivative.Text))
                : null;
            result.Add(new ResourceCandidate(attachment, item.Message.Id, ResourceKind.Text, TokenEstimator.Estimate(ResourceMaterial.Whole(attachment)), summaryTokens, rankedChunks));
        }

        return result;
    }

    /// <summary>
    /// Writes the summary of <paramref name="attachment"/> with the task's model and stores it (once per attachment and prompt
    /// version, whatever the model). A text longer than the model can take at once is summarized in consecutive runs of chunks
    /// first, then those summaries merged; never a summary of a summary of a summary. Throws
    /// <see cref="ContextCompressionException"/> when a request fails or returns nothing usable: the answer fails and can be retried.
    /// </summary>
    /// <param name="request">Makes one request whose complete output is used, recorded for the task with purpose resource_summary.</param>
    /// <returns>The estimated tokens of the summary's material section.</returns>
    public async Task<int> SummarizeAsync(
        ModelSnapshot model, AttachmentBlock attachment, Func<ChatRequest, Task<(string? Text, string? Problem)>> request, CancellationToken stop)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(request);
        var text = attachment.Text ?? throw new ArgumentException("Only a text attachment has a summary.", nameof(attachment));
        var instructions = PromptLibrary.Get("attachment-digest");
        var budget = ContextBudget.For(model);
        var room = Math.Max(2_000, budget.Available - TokenEstimator.Estimate(instructions) - 200);

        string summary;
        if (TokenEstimator.Estimate(text) <= room)
        {
            var material = T($"Material \"{attachment.DisplayName}\":\n\n{text}", $"材料《{attachment.DisplayName}》：\n\n{text}");
            summary = await OneAsync(model, instructions, material, attachment, request, stop).ConfigureAwait(false);
        }
        else
        {
            var segments = Segments(text, room);
            var parts = new List<string>();
            for (var i = 0; i < segments.Count; i++)
            {
                var material = T(
                    $"Material \"{attachment.DisplayName}\", part {i + 1}/{segments.Count}:\n\n{segments[i]}",
                    $"材料《{attachment.DisplayName}》的第 {i + 1}/{segments.Count} 段：\n\n{segments[i]}");
                parts.Add(await OneAsync(model, instructions, material, attachment, request, stop).ConfigureAwait(false));
            }

            var joined = new StringBuilder();
            for (var i = 0; i < parts.Count; i++)
            {
                joined.Append(T($"Digest of part {i + 1}:\n{parts[i]}\n\n", $"第 {i + 1} 段的摘要：\n{parts[i]}\n\n"));
            }

            summary = await OneAsync(model, PromptLibrary.Get("attachment-digest-merge"), joined.ToString(), attachment, request, stop).ConfigureAwait(false);
        }

        var tokens = TokenEstimator.Estimate(summary);
        await store.AddDerivativeAsync(
                attachment.Resource,
                new ResourceDerivative(DerivativeKind.Summary, summary, model.Model, SummaryPromptVersion, EstimatedTokens: tokens),
                clock.UtcNow,
                CancellationToken.None)
            .ConfigureAwait(false);
        LogSummarized(logger, attachment.Resource, tokens);
        return TokenEstimator.Estimate(ResourceMaterial.Summary(attachment, summary));
    }

    /// <summary>The material each planned message carries, by message: sections in plan order, then its images.</summary>
    public async Task<IReadOnlyDictionary<MessageId, TurnMaterials>> MaterialsAsync(ContextPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var result = new Dictionary<MessageId, TurnMaterials>();
        if (plan.Resources.Count == 0)
        {
            return result;
        }

        var ids = plan.Resources.Select(r => r.Resource).Distinct().ToList();
        var summaries = await store.GetDerivativesAsync(ids, DerivativeKind.Summary, SummaryPromptVersion, cancellationToken).ConfigureAwait(false);
        var chunks = await store.GetChunksAsync([.. plan.Resources.Where(r => r.Form == ResourceForm.Parts).Select(r => r.Resource)], cancellationToken).ConfigureAwait(false);
        foreach (var group in plan.Resources.GroupBy(r => r.Message))
        {
            var sections = new List<string>();
            var images = new List<ChatImage>();
            foreach (var placement in group)
            {
                var a = placement.Attachment;
                switch (placement.Form)
                {
                    case ResourceForm.Whole:
                        sections.Add(ResourceMaterial.Whole(a));
                        break;
                    case ResourceForm.Summary:
                        sections.Add(ResourceMaterial.Summary(a, SummaryOf(summaries, a)));
                        break;
                    case ResourceForm.Parts:
                        var all = chunks.TryGetValue(a.Resource, out var c) ? c : [];
                        var chosen = all.Where(x => placement.Chunks.Contains(x.Ordinal)).Select(x => (x.Ordinal, a.Text![x.Start..x.End])).ToList();
                        sections.Add(ResourceMaterial.Parts(a, SummaryOf(summaries, a), chosen, placement.ChunkCount));
                        break;
                    case ResourceForm.Image:
                        var stored = await store.GetAsync(a.Resource, cancellationToken).ConfigureAwait(false);
                        var image = stored is null ? null : await resources.ImageForSendingAsync(stored.Resource, cancellationToken).ConfigureAwait(false);
                        if (image is null)
                        {
                            sections.Add(PlaceholderOf(a, ResourceKind.ImageMissing));
                        }
                        else
                        {
                            sections.Add(ResourceMaterial.ImageLabel(a));
                            images.Add(new ChatImage(image.MediaType, image.Bytes));
                        }

                        break;
                    case ResourceForm.ImageOmitted:
                        sections.Add(ResourceMaterial.ImageOmitted(a));
                        break;
                    case ResourceForm.ImageUnsupported:
                        sections.Add(ResourceMaterial.ImageUnsupported(a));
                        break;
                    default:
                        sections.Add(PlaceholderOf(a, ResourceKind.ImageMissing));
                        break;
                }
            }

            result[group.Key] = new TurnMaterials(string.Join("\n\n", sections), images);
        }

        return result;
    }

    /// <summary>The record of how each attachment went out, for the task.</summary>
    public static IReadOnlyList<ResourceInput> InputsOf(ContextPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return [.. plan.Resources.Select(r => new ResourceInput(r.Resource, r.Attachment.DisplayName, r.Form, r.Chunks, r.ChunkCount))];
    }

    internal Task RecordAsync(GenerationTaskId task, ContextPlan plan) => RecordQuietlyAsync(task, InputsOf(plan));

    private async Task RecordQuietlyAsync(GenerationTaskId task, IReadOnlyList<ResourceInput> inputs)
    {
        try
        {
            await store.RecordTaskInputAsync(task, inputs, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Losing the record must not fail an answer whose context is ready.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogInputNotRecorded(logger, ex, task);
        }
    }

    private static string PlaceholderOf(AttachmentBlock attachment, ResourceKind kind) => kind == ResourceKind.ImageUnsupported
        ? ResourceMaterial.ImageUnsupported(attachment)
        : $"[图片：{attachment.DisplayName}，原件缺失]";

    private static string SummaryOf(IReadOnlyDictionary<ResourceId, StoredDerivative> summaries, AttachmentBlock attachment) =>
        summaries.TryGetValue(attachment.Resource, out var s)
            ? s.Derivative.Text
            : throw new ContextCompressionException($"附件《{attachment.DisplayName}》的摘要不在了，请重试。");

    /// <summary>Consecutive runs of the text's chunks, each within <paramref name="room"/> estimated tokens.</summary>
    private static List<string> Segments(string text, int room)
    {
        var segments = new List<string>();
        var start = 0;
        var size = 0;
        var chunks = ResourceChunker.Split(text);
        for (var i = 0; i < chunks.Count; i++)
        {
            if (size > 0 && size + chunks[i].EstimatedTokens > room)
            {
                segments.Add(text[start..chunks[i].Start]);
                start = chunks[i].Start;
                size = 0;
            }

            size += chunks[i].EstimatedTokens;
        }

        segments.Add(text[start..]);
        return segments;
    }

    private static async Task<string> OneAsync(
        ModelSnapshot model, string instructions, string material, AttachmentBlock attachment, Func<ChatRequest, Task<(string? Text, string? Problem)>> request, CancellationToken stop)
    {
        var turns = new[] { new ChatTurn(ChatRole.System, instructions), new ChatTurn(ChatRole.User, material) };
        var output = ContextCompressor.SummaryOutputCap(model, ChatTurns.Estimate(turns));
        (string? Text, string? Problem) result;
        try
        {
            result = await request(new ChatRequest(model with { MaxOutputTokens = Math.Min(output, SummaryOutputTokens) }, turns)).ConfigureAwait(false);
        }
        catch (ChatClientException ex)
        {
            throw new ContextCompressionException($"为附件《{attachment.DisplayName}》生成摘要时请求失败：{ex.Message}", ex);
        }

        stop.ThrowIfCancellationRequested();
        return result.Text ?? throw new ContextCompressionException($"为附件《{attachment.DisplayName}》生成摘要时服务没有返回完整内容（{result.Problem}）。");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Summarized resource {Resource} ({Tokens} estimated tokens)")]
    private static partial void LogSummarized(ILogger logger, ResourceId resource, int tokens);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Resource inputs of task {TaskId} were not recorded")]
    private static partial void LogInputNotRecorded(ILogger logger, Exception exception, GenerationTaskId taskId);
}
