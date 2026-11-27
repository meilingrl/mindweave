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
        ResourceSummaries = new RequestScript("resource summary", r => "附件摘要：" + r.Turns[^1].Content.Length + " 字", TruncatedSummaryText);
        Knowledge = new RequestScript("knowledge summary", KnowledgeOf);
        Naming = new RequestScript("naming", NamesOf);
        WorkspaceNames = new RequestScript("workspace names", _ => DefaultWorkspaceNames);
        Recommendations = new RequestScript("recommendations", _ => DefaultRecommendations);
        NodeRecommendations = new RequestScript("node recommendations", _ => DefaultNodeRecommendations);
        LinkSuggestions = new RequestScript("link suggestions", r => LinkSuggestionAnswer?.Invoke(r) ?? DefaultLinkSuggestions);
    }

    public ConcurrentQueue<ChatRequest> Requests { get; } = new();

    public int StartedAnswers => Requests.Count(IsAnswer);

    /// <summary>Automatic conversation titles.</summary>
    public RequestScript Titles { get; }

    /// <summary>Context summaries (S3): background standing in for older turns. A truncated one sends <see cref="TruncatedSummaryText"/>.</summary>
    public RequestScript ContextSummaries { get; }

    /// <summary>Summaries of an attachment (architecture §15.4): "附件摘要：n 字" of the material it was given.</summary>
    public RequestScript ResourceSummaries { get; }

    /// <summary>A summary of an attachment, or the merge of its segments' summaries.</summary>
    public static bool IsResourceSummary(ChatRequest request) => SystemSays(request, "材料写摘要", "digest of a piece of material") || SystemSays(request, "合成一份完整的摘要", "Combine them into one complete digest");

    /// <summary>Knowledge summaries of a conversation (S4); the default is <see cref="KnowledgeOf"/>.</summary>
    public RequestScript Knowledge { get; }

    /// <summary>Naming requests that tell similar summaries apart; the default is "n：区分n" for every node.</summary>
    public RequestScript Naming { get; }

    /// <summary>Names suggested for a workspace while renaming it (2026-11-11); the default is <see cref="DefaultWorkspaceNames"/>.</summary>
    public RequestScript WorkspaceNames { get; }

    public const string DefaultWorkspaceNames = "蒸汽机车史\n铁路发展\n火车与工业革命";

    /// <summary>Recommended questions (requirements §10.3.1); the default is <see cref="DefaultRecommendations"/>.</summary>
    public RequestScript Recommendations { get; }

    /// <summary>Three questions: the first two extend the first two summaries listed (S1, S2), the third none.</summary>
    public const string DefaultRecommendations =
        "[{\"question\":\"蒸汽机车为什么被电力机车取代？\",\"reason\":\"顺着火车的发展往后看\",\"basis\":\"S1\"}," +
        "{\"question\":\"高铁的轨道和普通铁轨有什么不同？\",\"reason\":\"从铁轨延伸\",\"basis\":\"S2\"}," +
        "{\"question\":\"火车的刹车是怎样工作的？\",\"reason\":\"还没问过的相邻方向\",\"basis\":\"\"}]";

    /// <summary>Recommended network nodes (requirements §10.3.2); the default is <see cref="DefaultNodeRecommendations"/>.</summary>
    public RequestScript NodeRecommendations { get; }

    /// <summary>Three directions, each extending one of the first three summaries listed (S1, S2, S3).</summary>
    public const string DefaultNodeRecommendations =
        "[{\"question\":\"蒸汽机车的锅炉为什么会爆炸？\",\"reason\":\"从动力往安全延伸\",\"basis\":\"S1\"}," +
        "{\"question\":\"铁路信号灯是怎样发展起来的？\",\"reason\":\"相邻但还没问过\",\"basis\":\"S2\"}," +
        "{\"question\":\"世界上最长的铁路隧道在哪里？\",\"reason\":\"换个角度看铁路\",\"basis\":\"S3\"}]";

    /// <summary>Suggested links between summaries (requirements §10.3.4); the default is <see cref="DefaultLinkSuggestions"/>.</summary>
    public RequestScript LinkSuggestions { get; }

    /// <summary>Two pairs of the first three summaries listed: S1–S2 "对比" and S2–S3 "原因", each with a reason.</summary>
    public const string DefaultLinkSuggestions =
        "[{\"a\":\"S1\",\"b\":\"S2\",\"note\":\"对比\",\"reason\":\"两者讲的是同一时期的不同技术路线\"}," +
        "{\"a\":\"S2\",\"b\":\"S3\",\"note\":\"原因\",\"reason\":\"后者解释了前者为什么出现\"}]";

    /// <summary>
    /// The reply to a link-suggestion request when no scripted one is queued; null for <see cref="DefaultLinkSuggestions"/>.
    /// <see cref="PairsByName"/> answers with pairs of summaries named as they are listed in the request.
    /// </summary>
    public Func<ChatRequest, string>? LinkSuggestionAnswer { get; set; }

    /// <summary>
    /// A link-suggestion answer naming summaries by their shown names: each name is replaced by the label ("S2") the request
    /// lists it under (an unlisted name is sent as it is, so the answer refers to nothing).
    /// </summary>
    public static string PairsByName(ChatRequest request, params (string A, string B, string? Note, string? Reason)[] pairs)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pairs);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in request.Turns[^1].Content.Split('\n'))
        {
            // The name runs up to the title (after "：") or the exploration note ("（探索 E1…）" / " (exploration E1…)").
            var match = System.Text.RegularExpressions.Regex.Match(line, @"^\[(S\d+)\] (.+?)(?:：|（探索 | \(exploration |$)");
            if (match.Success)
            {
                labels.TryAdd(match.Groups[2].Value.Trim(), match.Groups[1].Value);
            }
        }

        string Label(string name) => labels.GetValueOrDefault(name, name);
        return System.Text.Json.JsonSerializer.Serialize(pairs.Select(p => new Dictionary<string, string?> { ["a"] = Label(p.A), ["b"] = Label(p.B), ["note"] = p.Note, ["reason"] = p.Reason }), PairJson);
    }

    private static readonly System.Text.Json.JsonSerializerOptions PairJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A request for link suggestions.</summary>
    public static bool IsLinkSuggestion(ChatRequest request) => SystemSays(request, "可能相关的总结对", "pairs of summaries that may be related");

    /// <summary>A request for recommended nodes (its prompt requires a basis for each).</summary>
    public static bool IsNodeRecommendation(ChatRequest request) => IsRecommendation(request) && SystemSays(request, "必须延伸自", "must extend one of the summaries");

    /// <summary>A request for recommended questions or nodes.</summary>
    public static bool IsRecommendation(ChatRequest request) => SystemSays(request, "值得接着探索的新问题", "new questions worth exploring next");

    /// <summary>A request for a workspace's suggested names.</summary>
    public static bool IsWorkspaceNaming(ChatRequest request) => SystemSays(request, "为这个工作区起", "candidate names for this workspace");

    /// <summary>What a context summary cut off at the output limit says.</summary>
    public const string TruncatedSummaryText = "摘要：背景是";

    public static bool IsTitle(ChatRequest request) => SystemSays(request, "起一个简短标题", "Give the conversation below a short title");

    /// <summary>A context summary (S3): background standing in for older turns.</summary>
    public static bool IsSummary(ChatRequest request) => SystemSays(request, "背景摘要", "background summary of a long conversation");

    /// <summary>A knowledge summary of a conversation (S4).</summary>
    public static bool IsKnowledgeSummary(ChatRequest request) => SystemSays(request, "知识总结", "summary of an exploration conversation") && !IsNaming(request);

    public static bool IsNaming(ChatRequest request) => SystemSays(request, "区分词", "distinguishing term");

    public static bool IsAnswer(ChatRequest request) =>
        !IsTitle(request) && !IsSummary(request) && !IsResourceSummary(request) && !IsKnowledgeSummary(request) && !IsNaming(request) && !IsWorkspaceNaming(request) && !IsRecommendation(request) && !IsLinkSuggestion(request);

    /// <summary>
    /// The turns of an answer request after the answer instructions that open it (requirements §10.9), without the app's own
    /// notes that go with the question (<see cref="Application.Conversations.RequestNote"/>): what tests of the history look at.
    /// A context summary joined to the instructions' system turn stays, as its own system turn.
    /// </summary>
    public static IReadOnlyList<ChatTurn> Dialogue(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var turns = request.Turns.ToList();
        if (turns.Count == 0 || turns[0].Role != ChatRole.System)
        {
            return turns;
        }

        var first = turns[0].Content;
        foreach (var instructions in new[] { Application.Conversations.AnswerPrompt.For(true), Application.Conversations.AnswerPrompt.For(false) })
        {
            if (instructions.Length > 0 && first.StartsWith(instructions, StringComparison.Ordinal))
            {
                var rest = first[instructions.Length..].TrimStart('\n');
                if (rest.Length == 0)
                {
                    turns.RemoveAt(0);
                }
                else
                {
                    turns[0] = turns[0] with { Content = rest };
                }

                break;
            }
        }

        return [.. turns
            .Select(t => t.Role == ChatRole.User ? t with { Content = Application.Conversations.RequestNote.Without(t.Content) } : t)
            .Where(t => t.Role != ChatRole.User || t.Content.Length > 0 || t.Images is { Count: > 0 })];
    }

    /// <summary>
    /// The app's note this request ends with (<see cref="Application.Conversations.RequestNote"/>): the branch part, earlier
    /// material, the allowance or the instruction to answer, without its marks. Empty when it has none.
    /// </summary>
    public static string NoteOf(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var last = request.Turns.LastOrDefault(t => t.Role == ChatRole.User)?.Content ?? string.Empty;
        var open = Application.Conversations.RequestNote.Open + "\n";
        var close = "\n" + Application.Conversations.RequestNote.Close;
        return last.StartsWith(open, StringComparison.Ordinal) && last.IndexOf(close, StringComparison.Ordinal) is var end and >= 0
            ? last[open.Length..end]
            : string.Empty;
    }

    public const string DefaultSummaryTitle = "蒸汽机车";

    /// <summary>The default knowledge summary: titled <see cref="DefaultSummaryTitle"/>, saying how many turns it saw.</summary>
    public static string KnowledgeOf(ChatRequest request) =>
        $"标题：{DefaultSummaryTitle}\n\n总结了 {request.Turns.Count(t => t.Role != ChatRole.System) - 1} 条消息。";

    /// <summary>Whether the request's system turn says the prompt's Chinese or English phrase (the prompts follow the interface language).</summary>
    private static bool SystemSays(ChatRequest request, string chinese, string english) =>
        request.Turns[0] is { Role: ChatRole.System } first
        && (first.Content.Contains(chinese, StringComparison.Ordinal) || first.Content.Contains(english, StringComparison.Ordinal));

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
    /// <summary>The reasoning a held thinking reply (<see cref="HoldThinking"/>) sends, in order.</summary>
    public static readonly string[] ThinkingChunks = ["先想想火车从哪里来，", "再想想蒸汽机怎样推动车轮，", "然后作答。"];

    /// <summary>
    /// Like <see cref="Hold"/>, but once released the reply ends by calling <paramref name="calls"/> instead of finishing as
    /// an answer: a run's middle round that can be held (reasoning and text pushed meanwhile).
    /// </summary>
    public HeldReply HoldCalling(IReadOnlyList<ChatToolCall> calls, params string[] before)
    {
        var held = new HeldReply(before);
        _scripts.Enqueue(new Script(held, Calls: calls));
        return held;
    }

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

    /// <summary>The next answer fails as a service that answered with <paramref name="httpStatus"/> (and <c>Retry-After</c>) does.</summary>
    public void FailWithStatus(GenerationErrorKind kind, int? httpStatus, TimeSpan? retryAfter = null) =>
        _scripts.Enqueue(new Script(null, [], kind, "simulated", HttpStatus: httpStatus, RetryAfter: retryAfter));

    /// <summary>The next answer asks for one call of <paramref name="tool"/> and is then cut off at its output limit.</summary>
    public void CutOffCalling(string tool, string arguments) =>
        _scripts.Enqueue(new Script(null, [], Calls: [new ChatToolCall("call_" + Interlocked.Increment(ref _callIds), tool, arguments, IsJsonObject(arguments))], End: new ChatStreamEvent.Completed(Truncated: true)));

    /// <summary>The next answer streams <paramref name="before"/> and then throws <paramref name="crash"/> (not a client failure).</summary>
    public void Crash(Exception crash, params string[] before) => _scripts.Enqueue(new Script(null, before, Crash: crash));

    /// <summary>The next answer streams <paramref name="chunks"/> and ends as the service ends one cut off at its output limit.</summary>
    public void CutOff(params string[] chunks) => _scripts.Enqueue(new Script(null, chunks, End: new ChatStreamEvent.Completed(Truncated: true)));

    /// <summary>The next answer streams <paramref name="chunks"/> and ends as one stopped by the service's content filter.</summary>
    public void Filtered(params string[] chunks) => _scripts.Enqueue(new Script(null, chunks, End: new ChatStreamEvent.Completed(Truncated: true, Filtered: true)));

    /// <summary>The next answer streams <paramref name="chunks"/> and the stream then ends without the service saying it finished.</summary>
    public void EndWithoutFinishing(params string[] chunks) => _scripts.Enqueue(new Script(null, chunks, NoEnd: true));

    public void Reply(params string[] chunks) => _scripts.Enqueue(new Script(null, chunks));

    /// <summary>
    /// The next answer request (a run's model step) streams <paramref name="text"/> and then asks for <paramref name="calls"/>,
    /// as a service with native tool calling does (phase 2); <paramref name="reasoning"/> comes with them when given.
    /// </summary>
    public void CallTools(string text, IReadOnlyList<ChatToolCall> calls, string? reasoning = null) =>
        _scripts.Enqueue(new Script(null, text.Length == 0 ? [] : [text], Calls: calls, Reasoning: reasoning));

    /// <summary>The next answer request asks for one call of <paramref name="tool"/> with <paramref name="arguments"/> (JSON).</summary>
    public void CallTool(string tool, string arguments, string text = "") =>
        CallTools(text, [new ChatToolCall("call_" + Interlocked.Increment(ref _callIds), tool, arguments, IsJsonObject(arguments))]);

    /// <summary>Every answer request without a script asks for this call again (a model that keeps calling tools); null: answer as usual.</summary>
    public (string Tool, string Arguments)? KeepCalling { get; set; }

    private int _callIds;

    private static bool IsJsonObject(string arguments)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(arguments);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>Prompt tokens the service reports for a request, as a real one does at the end of its stream; null reports none.</summary>
    public Func<ChatRequest, int?>? PromptTokens { get; set; }

    /// <summary>Completion tokens the service reports for a request; null reports none.</summary>
    public Func<ChatRequest, int?>? CompletionTokens { get; set; }

    /// <summary>The input this request read from the provider's cache, reported with the usage; null: not reported.</summary>
    public Func<ChatRequest, int?>? CacheReadTokens { get; set; }

    /// <summary>The input this request wrote to the provider's cache, reported with the usage; null: not reported.</summary>
    public Func<ChatRequest, int?>? CacheWriteTokens { get; set; }

    /// <summary>Every (address, key) pair sent to the service, by listing models or generating, in order.</summary>
    public ConcurrentQueue<(string BaseUrl, string ApiKey)> KeysSent { get; } = new();

    public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        KeysSent.Enqueue((request.Model.BaseUrl, apiKey));
        Requests.Enqueue(request);
        await Task.Yield();
        if (ReasonFirst)
        {
            yield return new ChatStreamEvent.ReasoningDelta("先想一想。");
            yield return new ChatStreamEvent.ReasoningDelta("再想一想。");
        }

        var billed = PromptTokens?.Invoke(request);
        var produced = CompletionTokens?.Invoke(request);
        if (billed is not null || produced is not null)
        {
            yield return new ChatStreamEvent.Usage(billed, produced, CacheReadTokens?.Invoke(request), CacheWriteTokens?.Invoke(request));
        }

        if (ScriptFor(request) is { } internalRequest)
        {
            await foreach (var item in internalRequest.StreamAsync(request, cancellationToken))
            {
                yield return item;
            }

            yield break;
        }

        if (!_scripts.TryDequeue(out var script) && KeepCalling is { } keep && request.HasTools)
        {
            script = new Script(null, [], Calls: [new ChatToolCall("call_" + Interlocked.Increment(ref _callIds), keep.Tool, keep.Arguments, IsJsonObject(keep.Arguments))]);
        }

        if (script is null)
        {
            yield return new ChatStreamEvent.TextDelta("答：");
            yield return new ChatStreamEvent.TextDelta(Application.Conversations.RequestNote.Without(request.Turns[^1].Content));
            yield return new ChatStreamEvent.Completed(false);
            yield break;
        }

        for (var i = 0; i < (script.Held?.ReasoningChunks ?? 0); i++)
        {
            yield return new ChatStreamEvent.ReasoningDelta(ThinkingChunks[i % ThinkingChunks.Length]);
        }

        if (script.Reasoning is { Length: > 0 } reasoning)
        {
            // A reply calling tools with reasoning (CallTools): the reasoning streams first, as a reasoning model's does.
            yield return new ChatStreamEvent.ReasoningDelta(reasoning);
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
                    yield return pushed;
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

        if (script.Crash is { } crash)
        {
            throw crash;
        }

        if (script.FailWith is { } kind)
        {
            throw new ChatClientException(kind, script.Detail!, httpStatus: script.HttpStatus) { RetryAfter = script.RetryAfter };
        }

        if (script.Calls is { } calls)
        {
            yield return new ChatStreamEvent.ToolCalls(calls, script.Reasoning);
        }

        if (script.NoEnd)
        {
            yield break;
        }

        yield return script.End ?? new ChatStreamEvent.Completed(false);
    }

    /// <summary>The script for an internal request, or null for an answer.</summary>
    private RequestScript? ScriptFor(ChatRequest request) =>
        IsSummary(request) ? ContextSummaries
        : IsResourceSummary(request) ? ResourceSummaries
        : IsKnowledgeSummary(request) ? Knowledge
        : IsNaming(request) ? Naming
        : IsTitle(request) ? Titles
        : IsWorkspaceNaming(request) ? WorkspaceNames
        : IsNodeRecommendation(request) ? NodeRecommendations
        : IsRecommendation(request) ? Recommendations
        : IsLinkSuggestion(request) ? LinkSuggestions
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

        /// <summary>Text and reasoning the held reply sends now, before it is released.</summary>
        internal Channel<ChatStreamEvent> Pushed { get; } = Channel.CreateUnbounded<ChatStreamEvent>();

        /// <summary>Streams <paramref name="chunks"/> while the reply stays held, as a service still writing does.</summary>
        public void Push(params string[] chunks)
        {
            foreach (var chunk in chunks)
            {
                Pushed.Writer.TryWrite(new ChatStreamEvent.TextDelta(chunk));
            }
        }

        /// <summary>Streams reasoning while the reply stays held, as a reasoning model still thinking does.</summary>
        public void Think(params string[] chunks)
        {
            foreach (var chunk in chunks)
            {
                Pushed.Writer.TryWrite(new ChatStreamEvent.ReasoningDelta(chunk));
            }
        }

        public void Finish(params string[] after)
        {
            After.AddRange(after);
            Release.TrySetResult();
        }
    }

    private sealed record Script(HeldReply? Held, string[]? Chunks = null, GenerationErrorKind? FailWith = null, string? Detail = null, Exception? Crash = null, ChatStreamEvent.Completed? End = null, bool NoEnd = false, IReadOnlyList<ChatToolCall>? Calls = null, string? Reasoning = null, int? HttpStatus = null, TimeSpan? RetryAfter = null);
}
