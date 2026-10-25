using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Mindweave.Application.Ai;
using Mindweave.Core.Conversations;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Scriptable AI service. By default answers "答：{question}" and titles with <see cref="DefaultTitle"/>.
/// <see cref="Hold"/> makes the next answer emit some text and then wait until released or cancelled.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    public const string DefaultTitle = "火车历史";

    private readonly ConcurrentQueue<Script> _scripts = new();

    public ConcurrentQueue<ChatRequest> Requests { get; } = new();

    public int StartedAnswers => Requests.Count(r => r.Turns[0].Role != ChatRole.System);

    public HeldReply Hold(params string[] before)
    {
        var held = new HeldReply(before);
        _scripts.Enqueue(new Script(held));
        return held;
    }

    public void Fail(GenerationErrorKind kind, string detail = "simulated", params string[] before) =>
        _scripts.Enqueue(new Script(null, before, kind, detail));

    public void Reply(params string[] chunks) => _scripts.Enqueue(new Script(null, chunks));

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        await Task.Yield();
        if (request.Turns[0].Role == ChatRole.System)
        {
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
