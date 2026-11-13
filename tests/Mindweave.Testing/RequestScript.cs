using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Mindweave.Application.Ai;
using Mindweave.Core.Conversations;

namespace Mindweave.Testing;

/// <summary>
/// How <see cref="FakeChatClient"/> answers one kind of internal request (titles, context summaries, knowledge summaries,
/// naming). Every kind has the same knobs: a <see cref="Gate"/> to hold requests, failures and truncations of the next
/// requests, and scripted <see cref="Replies"/> used in order before the kind's default reply.
/// </summary>
public sealed class RequestScript
{
    private readonly string _name;
    private readonly Func<ChatRequest, string> _defaultReply;
    private readonly string? _truncatedText;
    private readonly Lock _lock = new();
    private GenerationErrorKind _failure;
    private int _failures;
    private int _truncations;
    private Exception? _crash;

    internal RequestScript(string name, Func<ChatRequest, string> defaultReply, string? truncatedText = null)
    {
        _name = name;
        _defaultReply = defaultReply;
        _truncatedText = truncatedText;
    }

    /// <summary>Requests of this kind wait for this before answering, when set; it stays in place until replaced.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Scripted outputs, used in order; when empty, the kind's default reply.</summary>
    public ConcurrentQueue<string> Replies { get; } = new();

    /// <summary>Sets a new <see cref="Gate"/> and returns it: requests of this kind wait until it is completed.</summary>
    public TaskCompletionSource Hold()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Gate = gate;
        return gate;
    }

    /// <summary>The next <paramref name="count"/> requests of this kind fail with <paramref name="kind"/>, as a failing service does.</summary>
    public void FailNext(GenerationErrorKind kind, int count = 1)
    {
        lock (_lock)
        {
            _failure = kind;
            _failures = count;
        }
    }

    /// <summary>The next request of this kind throws <paramref name="crash"/>, as a defect or an unforeseen response shape would.</summary>
    public void CrashNext(Exception crash) => Interlocked.Exchange(ref _crash, crash);

    /// <summary>
    /// The next <paramref name="count"/> requests of this kind stop at the output limit (a truncated finish), as a
    /// reasoning model does when it spends the limit on reasoning.
    /// </summary>
    public void TruncateNext(int count = 1) => Interlocked.Exchange(ref _truncations, count);

    internal async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (Interlocked.Exchange(ref _crash, null) is { } crash)
        {
            throw crash;
        }

        if (TakeFailure() is { } failure)
        {
            throw new ChatClientException(failure, $"simulated {_name} failure");
        }

        var truncated = TakeTruncation();
        if (truncated && _truncatedText is { } cut)
        {
            yield return new ChatStreamEvent.TextDelta(cut);
        }
        else
        {
            yield return new ChatStreamEvent.TextDelta(Replies.TryDequeue(out var scripted) ? scripted : _defaultReply(request));
        }

        yield return new ChatStreamEvent.Completed(truncated);
    }

    private GenerationErrorKind? TakeFailure()
    {
        lock (_lock)
        {
            if (_failures <= 0)
            {
                return null;
            }

            _failures--;
            return _failure;
        }
    }

    private bool TakeTruncation()
    {
        while (true)
        {
            var left = Volatile.Read(ref _truncations);
            if (left <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _truncations, left - 1, left) == left)
            {
                return true;
            }
        }
    }
}
