using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Mindweave.Application.Ai;
using Mindweave.Core.Conversations;

namespace Mindweave.Testing;

/// <summary>
/// Scriptable AI service. By default answers "答：{question}" and titles with <see cref="DefaultTitle"/>.
/// <see cref="Hold"/> makes the next answer emit some text and then wait until released or cancelled.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    public const string DefaultTitle = "火车历史";

    private readonly ConcurrentQueue<Script> _scripts = new();
    private volatile TaskCompletionSource? _titleGate;

    public ConcurrentQueue<ChatRequest> Requests { get; } = new();

    public int StartedAnswers => Requests.Count(IsAnswer);

    /// <summary>Fails the next summary requests with this kind, as a failing service would.</summary>
    public GenerationErrorKind? FailSummaries { get; set; }

    /// <summary>
    /// How many of the next summary requests stop at the output limit, as a reasoning model does when it spends the
    /// limit on reasoning: <see cref="TruncatedSummaryText"/> then a truncated finish.
    /// </summary>
    public int TruncateSummaries { get; set; }

    public const string TruncatedSummaryText = "摘要：背景是";

    /// <summary>Summary requests wait for this before answering, when set.</summary>
    public TaskCompletionSource? SummaryGate { get; set; }

    public static bool IsTitle(ChatRequest request) => request.Turns[0] is { Role: ChatRole.System } first && first.Content.Contains("标题", StringComparison.Ordinal);

    public static bool IsSummary(ChatRequest request) => request.Turns[0] is { Role: ChatRole.System } first && first.Content.Contains("背景摘要", StringComparison.Ordinal);

    public static bool IsAnswer(ChatRequest request) => !IsTitle(request) && !IsSummary(request);

    /// <summary>The summary the fake writes for a request: derived from its input, so tests can see what was covered.</summary>
    public static string SummaryOf(ChatRequest request) => "摘要：" + request.Turns[^1].Content.Length + " 字";

    public HeldReply Hold(params string[] before)
    {
        var held = new HeldReply(before);
        _scripts.Enqueue(new Script(held));
        return held;
    }

    public void Fail(GenerationErrorKind kind, string detail = "simulated", params string[] before) =>
        _scripts.Enqueue(new Script(null, before, kind, detail));

    public void Reply(params string[] chunks) => _scripts.Enqueue(new Script(null, chunks));

    /// <summary>Title requests wait until the returned source is completed, as with a slow service.</summary>
    public TaskCompletionSource HoldTitles()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _titleGate = gate;
        return gate;
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        await Task.Yield();
        if (IsSummary(request))
        {
            if (SummaryGate is { } summaryGate)
            {
                await summaryGate.Task.WaitAsync(cancellationToken);
            }

            if (FailSummaries is { } failure)
            {
                throw new ChatClientException(failure, "simulated summary failure");
            }

            if (TruncateSummaries > 0)
            {
                TruncateSummaries--;
                yield return new ChatStreamEvent.TextDelta(TruncatedSummaryText);
                yield return new ChatStreamEvent.Completed(Truncated: true);
                yield break;
            }

            yield return new ChatStreamEvent.TextDelta(SummaryOf(request));
            yield return new ChatStreamEvent.Completed(false);
            yield break;
        }

        if (IsTitle(request))
        {
            if (_titleGate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            yield return new ChatStreamEvent.TextDelta($"“{DefaultTitle}”");
            yield return new ChatStreamEvent.Completed(false);
            yield break;
        }

        if (!_scripts.TryDequeue(out var script))
        {
            yield return new ChatStreamEvent.TextDelta("答：");
            yield return new ChatStreamEvent.TextDelta(request.Turns[^1].Content);
            yield return new ChatStreamEvent.Completed(false);
            yield break;
        }

        foreach (var chunk in script.Held?.Before ?? script.Chunks ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatStreamEvent.TextDelta(chunk);
        }

        if (script.Held is { } held)
        {
            held.Started.TrySetResult();
            await held.Release.Task.WaitAsync(cancellationToken);
            foreach (var chunk in held.After)
            {
                yield return new ChatStreamEvent.TextDelta(chunk);
            }
        }

        if (script.FailWith is { } kind)
        {
            throw new ChatClientException(kind, script.Detail!);
        }

        yield return new ChatStreamEvent.Completed(false);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(["fake-model"]);

    public sealed class HeldReply(string[] before)
    {
        public string[] Before { get; } = before;

        public List<string> After { get; } = [];

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Finish(params string[] after)
        {
            After.AddRange(after);
            Release.TrySetResult();
        }
    }

    private sealed record Script(HeldReply? Held, string[]? Chunks = null, GenerationErrorKind? FailWith = null, string? Detail = null);
}
