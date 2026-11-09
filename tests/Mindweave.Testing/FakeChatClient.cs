using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Mindweave.Application.Ai;
using Mindweave.Core.Conversations;

namespace Mindweave.Testing;

/// <summary>
/// Scriptable AI service. By default answers "答：{question}" and titles with <see cref="DefaultTitle"/>.
/// <see cref="Hold"/> makes the next answer emit some text and then wait until released or cancelled. Internal requests
/// (titles, context summaries, knowledge summaries, naming) follow their own <see cref="RequestScript"/>.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    public const string DefaultTitle = "火车历史";

    private readonly ConcurrentQueue<Script> _scripts = new();

    public FakeChatClient()
    {
        Titles = new RequestScript("title", _ => $"“{DefaultTitle}”");
        ContextSummaries = new RequestScript("summary", SummaryOf, TruncatedSummaryText);
        Knowledge = new RequestScript("knowledge summary", KnowledgeOf);
        Naming = new RequestScript("naming", NamesOf);
    }

    public ConcurrentQueue<ChatRequest> Requests { get; } = new();

    public int StartedAnswers => Requests.Count(IsAnswer);

    /// <summary>Automatic conversation titles.</summary>
    public RequestScript Titles { get; }

    /// <summary>Context summaries (S3): background standing in for older turns. A truncated one sends <see cref="TruncatedSummaryText"/>.</summary>
    public RequestScript ContextSummaries { get; }

    /// <summary>Knowledge summaries of a conversation (S4); the default is <see cref="KnowledgeOf"/>.</summary>
    public RequestScript Knowledge { get; }

    /// <summary>Naming requests that tell similar summaries apart; the default is "n：区分n" for every node.</summary>
    public RequestScript Naming { get; }

    /// <summary>What a context summary cut off at the output limit says.</summary>
    public const string TruncatedSummaryText = "摘要：背景是";

    public static bool IsTitle(ChatRequest request) => SystemSays(request, "起一个简短标题");

    /// <summary>A context summary (S3): background standing in for older turns.</summary>
    public static bool IsSummary(ChatRequest request) => SystemSays(request, "背景摘要");

    /// <summary>A knowledge summary of a conversation (S4).</summary>
    public static bool IsKnowledgeSummary(ChatRequest request) => SystemSays(request, "知识总结") && !IsNaming(request);

    public static bool IsNaming(ChatRequest request) => SystemSays(request, "区分词");

    public static bool IsAnswer(ChatRequest request) => !IsTitle(request) && !IsSummary(request) && !IsKnowledgeSummary(request) && !IsNaming(request);

    public const string DefaultSummaryTitle = "蒸汽机车";

    /// <summary>The default knowledge summary: titled <see cref="DefaultSummaryTitle"/>, saying how many turns it saw.</summary>
    public static string KnowledgeOf(ChatRequest request) =>
        $"标题：{DefaultSummaryTitle}\n\n总结了 {request.Turns.Count(t => t.Role != ChatRole.System) - 1} 条消息。";

    private static bool SystemSays(ChatRequest request, string phrase) =>
        request.Turns[0] is { Role: ChatRole.System } first && first.Content.Contains(phrase, StringComparison.Ordinal);

    /// <summary>The summary the fake writes for a request: derived from its input, so tests can see what was covered.</summary>
    public static string SummaryOf(ChatRequest request) => "摘要：" + request.Turns[^1].Content.Length + " 字";

    /// <summary>The default names: "n：区分n" for every node listed in the request.</summary>
    private static string NamesOf(ChatRequest request)
    {
        var count = request.Turns[^1].Content.Split('\n').Count(l => l.StartsWith('['));
        return string.Join('\n', Enumerable.Range(1, count).Select(i => $"{i}：区分{i}"));
    }

    public HeldReply Hold(params string[] before)
    {
        var held = new HeldReply(before);
        _scripts.Enqueue(new Script(held));
        return held;
    }

    /// <summary>
    /// Like <see cref="Hold"/>, as a reasoning model: the next answer sends reasoning first, then <paramref name="before"/>
    /// (usually nothing), and waits.
    /// </summary>
    public HeldReply HoldThinking(params string[] before)
    {
        var held = new HeldReply(before) { ReasoningChunks = 3 };
        _scripts.Enqueue(new Script(held));
        return held;
    }

    /// <summary>
    /// Every stream (answers, titles, summaries, naming) starts with reasoning, as a reasoning model's does before its text.
    /// </summary>
    public bool ReasonFirst { get; set; }

    public void Fail(GenerationErrorKind kind, string detail = "simulated", params string[] before) =>
        _scripts.Enqueue(new Script(null, before, kind, detail));

    public void Reply(params string[] chunks) => _scripts.Enqueue(new Script(null, chunks));

    /// <summary>Prompt tokens the service reports for a request, as a real one does at the end of its stream; null reports none.</summary>
    public Func<ChatRequest, int?>? PromptTokens { get; set; }

    /// <summary>Every (address, key) pair sent to the service, by listing models or generating, in order.</summary>
    public ConcurrentQueue<(string BaseUrl, string ApiKey)> KeysSent { get; } = new();

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        KeysSent.Enqueue((request.Model.BaseUrl, apiKey));
        Requests.Enqueue(request);
        await Task.Yield();
        if (ReasonFirst)
        {
            yield return new ChatStreamEvent.ReasoningDelta(12);
            yield return new ChatStreamEvent.ReasoningDelta(30);
        }

        if (PromptTokens?.Invoke(request) is { } billed)
        {
            yield return new ChatStreamEvent.Usage(billed, null);
        }

        if (ScriptFor(request) is { } internalRequest)
        {
            await foreach (var item in internalRequest.StreamAsync(request, cancellationToken))
            {
                yield return item;
            }

            yield break;
        }

        if (!_scripts.TryDequeue(out var script))
        {
            yield return new ChatStreamEvent.TextDelta("答：");
            yield return new ChatStreamEvent.TextDelta(request.Turns[^1].Content);
            yield return new ChatStreamEvent.Completed(false);
            yield break;
        }

        for (var i = 0; i < (script.Held?.ReasoningChunks ?? 0); i++)
        {
            yield return new ChatStreamEvent.ReasoningDelta(20);
        }

        foreach (var chunk in script.Held?.Before ?? script.Chunks ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatStreamEvent.TextDelta(chunk);
        }

        if (script.Held is { } held)
        {
            held.Started.TrySetResult();

            // Text pushed while held streams at once; the reply waits for more until released. Release is read before
            // the drain: text pushed before the release is then in the channel and is sent, never skipped.
            while (true)
            {
                var released = held.Release.Task.IsCompleted;
                while (held.Pushed.Reader.TryRead(out var pushed))
                {
                    yield return new ChatStreamEvent.TextDelta(pushed);
                }

                if (released)
                {
                    break;
                }

                await Task.WhenAny(held.Pushed.Reader.WaitToReadAsync(cancellationToken).AsTask(), held.Release.Task).WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
            }

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

    /// <summary>The script for an internal request, or null for an answer.</summary>
    private RequestScript? ScriptFor(ChatRequest request) =>
        IsSummary(request) ? ContextSummaries
        : IsKnowledgeSummary(request) ? Knowledge
        : IsNaming(request) ? Naming
        : IsTitle(request) ? Titles
        : null;

    /// <summary>What listing the models returns.</summary>
    public IReadOnlyList<string> Models { get; set; } = ["fake-model"];

    public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        KeysSent.Enqueue((baseUrl, apiKey));
        return Task.FromResult(Models);
    }

    public sealed class HeldReply(string[] before)
    {
        public string[] Before { get; } = before;

        /// <summary>How many reasoning chunks come before <see cref="Before"/>.</summary>
        public int ReasoningChunks { get; init; }

        public List<string> After { get; } = [];

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Text the held reply sends now, before it is released.</summary>
        internal Channel<string> Pushed { get; } = Channel.CreateUnbounded<string>();

        /// <summary>Streams <paramref name="chunks"/> while the reply stays held, as a service still writing does.</summary>
        public void Push(params string[] chunks)
        {
            foreach (var chunk in chunks)
            {
                Pushed.Writer.TryWrite(chunk);
            }
        }

        public void Finish(params string[] after)
        {
            After.AddRange(after);
            Release.TrySetResult();
        }
    }

    private sealed record Script(HeldReply? Held, string[]? Chunks = null, GenerationErrorKind? FailWith = null, string? Detail = null);
}
