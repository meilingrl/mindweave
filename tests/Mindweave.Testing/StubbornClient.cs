using System.Runtime.CompilerServices;
using Mindweave.Application.Ai;

namespace Mindweave.Testing;

/// <summary>Streams one piece of an answer, then waits for <see cref="Release"/> without honouring cancellation.</summary>
public sealed class StubbornClient : IChatClient
{
    private readonly FakeChatClient _fallback = new();

    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!FakeChatClient.IsAnswer(request))
        {
            await foreach (var item in _fallback.StreamAsync(request, apiKey, cancellationToken))
            {
                yield return item;
            }

            yield break;
        }

        yield return new ChatStreamEvent.TextDelta("已收到");
        Started.TrySetResult();
        await Release.Task;
        yield return new ChatStreamEvent.Completed(false);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken) =>
        _fallback.ListModelsAsync(baseUrl, apiKey, cancellationToken);
}
