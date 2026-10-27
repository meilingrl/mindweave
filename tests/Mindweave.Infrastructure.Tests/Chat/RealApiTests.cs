using Mindweave.Application.Models;
using Mindweave.Application.Ai;
using Mindweave.Core.Context;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Ai;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Handoff S1 acceptance against a real service: long streamed answer, stop mid-stream, multi-turn context,
/// automatic title. Opt-in through scripts/real-api.sh, which supplies the key via the environment only.
/// A full run makes about 25 short requests.
/// </summary>
[Trait("Category", "RealApi")]
public class RealApiTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("MINDWEAVE_REAL_API_URL");
    private static readonly string? Model = Environment.GetEnvironmentVariable("MINDWEAVE_REAL_API_MODEL");
    private static readonly string? Key = Environment.GetEnvironmentVariable("MINDWEAVE_REAL_API_KEY");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_long_answer_streams_completes_and_gets_a_title()
    {
        await using var h = await StartAsync();

        var sent = await h.Conversations.StartAsync(h.Workspace, "请用大约 600 字介绍蒸汽机车的发明过程，分几个段落。", Ct);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value, timeoutMs: 180_000);

        Assert.True(answer.State == MessageState.Complete, $"{answer.State}: {answer.ErrorKind} {answer.ErrorDetail}");
        Assert.True(answer.Text.Length > 200, $"Only {answer.Text.Length} characters.");
        await ChatHarness.UntilAsync(() => h.Conversations.LoadAsync(sent.Conversation, Ct).Result!.Conversation.TitleSource == TitleSource.Auto, 60_000);
        var title = (await h.Conversations.LoadAsync(sent.Conversation, Ct))!.Conversation.Title;
        TestContext.Current.SendDiagnosticMessage($"{answer.Text.Length} chars; title: {title}");
    }

    [Fact]
    public async Task Stopping_mid_stream_keeps_the_partial_answer()
    {
        await using var h = await StartAsync();

        var sent = await h.Conversations.StartAsync(h.Workspace, "请详细讲述铁路发展史，至少 1500 字。", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value, m => m.Text.Length > 20, timeoutMs: 120_000);
        await h.Conversations.StopAsync(sent.Answer!.Value);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(MessageState.Stopped, answer.State);
        Assert.NotEmpty(answer.Text);
    }

    [Fact]
    public async Task Follow_up_questions_see_the_earlier_turns()
    {
        await using var h = await StartAsync();

        var first = await h.Conversations.StartAsync(h.Workspace, "请记住这个代号：青鸾-47。只回答“好的”。", Ct);
        await h.WaitForAnswerAsync(first.Answer!.Value, timeoutMs: 120_000);
        var second = await h.Conversations.AskAsync(first.Conversation, "我刚才让你记住的代号是什么？只回答代号。", Ct);
        var answer = await h.WaitForAnswerAsync(second.Answer!.Value, timeoutMs: 120_000);

        Assert.Equal(MessageState.Complete, answer.State);
        Assert.Contains("47", answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_branch_question_is_answered_about_the_quoted_passage()
    {
        await using var h = await StartAsync();

        var root = await h.Conversations.StartAsync(h.Workspace, "请原样回答这一句话，不要加别的内容：甲方案的代号是蓝鲸。乙方案的代号是赤狐。", Ct);
        var source = await h.WaitForAnswerAsync(root.Answer!.Value, timeoutMs: 120_000);
        Assert.Contains("赤狐", source.Text, StringComparison.Ordinal);
        var quote = source.Text[source.Text.IndexOf("乙方案", StringComparison.Ordinal)..].Trim();
        var branch = await h.Conversations.CreateBranchAsync(root.Conversation, BranchFlowTests.Selection(source, quote), Ct);
        Assert.True(branch.Accepted);

        var asked = await h.Conversations.AskAsync(branch.Child, "这段内容里提到的代号是什么？只回答代号。", Ct);
        var answer = await h.WaitForAnswerAsync(asked.Answer!.Value, timeoutMs: 120_000);

        Assert.Equal(MessageState.Complete, answer.State);
        Assert.Contains("赤狐", answer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("蓝鲸", answer.Text, StringComparison.Ordinal);
        TestContext.Current.SendDiagnosticMessage($"quote: {quote}; branch answer: {answer.Text}");
    }

    [Fact]
    public async Task The_service_lists_models()
    {
        Assert.SkipWhen(Key is null, "Run through scripts/real-api.sh test.");
        using var http = new HttpClient(ChatCompletionsClient.CreateHandler());
        var client = new ChatCompletionsClient(http, new ChatClientOptions());

        var models = await client.ListModelsAsync(ModelSettingsService.NormalizeBaseUrl(Url!), Key!, Ct);

        TestContext.Current.SendDiagnosticMessage($"{models.Count} models; configured model listed: {models.Contains(Model!)}");
        Assert.NotEmpty(models);
    }

    [Fact]
    public async Task A_compressed_long_conversation_keeps_corrections_and_references()
    {
        // A small declared capacity (budget 7,500 − 4,096 − 1,000 = 2,404) forces compression after a few turns.
        var recorder = new RecordingClient();
        await using var h = await StartAsync(contextWindow: 7_500, recorder);
        var (conversation, _) = await AskAsync(h, null, "我在研究早期蒸汽机车。请记住：之后我说“那台车”，都指斯蒂芬森父子造的“火箭号”。只回答“好的”。");
        await AskAsync(h, conversation, "我研究的年份是 1825 年的斯托克顿—达灵顿铁路。只回答“好的”。");
        await AskAsync(h, conversation, "我刚才说错了，我研究的不是 1825 年，而是 1829 年的雨山试验。只回答“已更正”。");
        foreach (var topic in new[] { "英国早期铁路的轨距之争", "蒸汽机车锅炉的改进", "早期铁路对煤炭运输的影响", "铁路时刻表与标准时间的出现", "早期铁路的信号系统", "铁路对城市发展的影响" })
        {
            await AskAsync(h, conversation, $"请用大约 400 字介绍{topic}。");
        }

        var (_, final) = await AskAsync(h, conversation, "那台车是谁造的？我研究的是哪一年的什么试验？请用一句话回答。");

        Assert.Contains(final.Id, await h.Summaries.CompressedAnswersAsync(conversation, Ct));
        Assert.Contains("斯蒂芬森", final.Text, StringComparison.Ordinal);
        Assert.Contains("1829", final.Text, StringComparison.Ordinal);
        Assert.Contains("雨山", final.Text, StringComparison.Ordinal);
        var summaries = await h.Summaries.ListAsync([conversation], Ct);
        TestContext.Current.SendDiagnosticMessage($"final: {final.Text}");
        foreach (var summary in summaries)
        {
            TestContext.Current.SendDiagnosticMessage($"summary {summary.Key.FromSeq}-{summary.Key.ToSeq}: {summary.Text}");
        }

        // The estimate must not undercount what the service bills as prompt tokens.
        var traces = await h.Database.ReadAsync(
            c =>
            {
                // Titles run in parallel; answers and their summaries in one conversation run strictly one after another.
                using var command = Sqlite.SqliteCommands.Command(c, null, """
                    SELECT r.prompt_tokens FROM ai_request r JOIN generation_task t ON t.id = r.task_id
                    WHERE t.kind = 'answer' ORDER BY r.started_at, r.rowid;
                    """);
                using var reader = command.ExecuteReader();
                var list = new List<int?>();
                while (reader.Read())
                {
                    list.Add(reader.IsDBNull(0) ? null : reader.GetInt32(0));
                }

                return list;
            },
            Ct);
        var sent = recorder.Requests.Where(r => !(r.Turns[0] is { Role: ChatRole.System } first && first.Content.Contains("标题", StringComparison.Ordinal))).ToList();
        Assert.Equal(sent.Count, traces.Count);
        var pairs = sent.Zip(traces).Where(p => p.Second is not null).ToList();
        Assert.NotEmpty(pairs);
        var compared = pairs.Select(p => (Estimate: TokenEstimator.PerRequest + p.First.Turns.Sum(t => TokenEstimator.Message(t.Content)), Reported: p.Second!.Value)).ToList();
        foreach (var (estimate, reported) in compared)
        {
            TestContext.Current.SendDiagnosticMessage($"estimate {estimate} / reported {reported}");
        }

        var table = string.Join("; ", compared.Select(c => $"{c.Estimate}/{c.Reported}"));
        Assert.True(compared.All(c => c.Estimate >= c.Reported), $"Estimate below reported prompt tokens (estimate/reported): {table}");
    }

    [Fact]
    public async Task A_summary_from_a_real_service_parses_and_keeps_the_corrected_fact()
    {
        await using var h = await StartAsync();
        var (conversation, _) = await AskAsync(h, null, "我在研究早期蒸汽机车。我研究的年份是 1825 年的斯托克顿—达灵顿铁路。请用两三句话介绍它。");
        await AskAsync(h, conversation, "我刚才说错了，我研究的不是 1825 年，而是 1829 年的雨山试验。请用两三句话介绍雨山试验。");

        var outcome = await h.SummaryService.SummarizeAsync(conversation, Ct);
        Assert.True(outcome.Accepted, outcome.Rejection?.ToString());
        await ChatHarness.UntilAsync(
            () => h.SummaryService.LatestTaskAsync(conversation, Ct).Result is { State: not (Application.Storage.GenerationTaskState.Queued or Application.Storage.GenerationTaskState.Running) },
            240_000);
        var task = (await h.SummaryService.LatestTaskAsync(conversation, Ct))!;
        Assert.True(task.State == Application.Storage.GenerationTaskState.Completed, $"{task.State}: {task.Error} {task.Detail}");

        var entry = (await h.SummaryService.ForConversationAsync(conversation, Ct))!;
        TestContext.Current.SendDiagnosticMessage($"title: {entry.Current.Title}\n{entry.Current.Body}");
        Assert.InRange(entry.Current.Title.Length, 1, 40);
        Assert.Contains("1829", entry.Current.Body, StringComparison.Ordinal);
        Assert.Contains("雨山", entry.Current.Body, StringComparison.Ordinal);
    }

    private static async Task<(ConversationId Conversation, Message Answer)> AskAsync(ChatHarness h, ConversationId? conversation, string question)
    {
        var sent = conversation is { } id
            ? await h.Conversations.AskAsync(id, question, Ct)
            : await h.Conversations.StartAsync(h.Workspace, question, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value, timeoutMs: 180_000);
        Assert.True(answer.State == MessageState.Complete, $"{answer.State}: {answer.ErrorKind} {answer.ErrorDetail}");
        return (sent.Conversation, answer);
    }

    private static async Task<ChatHarness> StartAsync(int? contextWindow = null, RecordingClient? recorder = null)
    {
        Assert.SkipWhen(Key is null || Url is null || Model is null, "Run through scripts/real-api.sh test.");
        var http = new HttpClient(ChatCompletionsClient.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        IChatClient client = new ChatCompletionsClient(http, new ChatClientOptions());
        if (recorder is not null)
        {
            recorder.Inner = client;
            client = recorder;
        }

        return await ChatHarness.StartAsync(client: client, model: new ModelSettingsInput("real", Url!, Model!, Key!, contextWindow));
    }

    /// <summary>Passes requests through and keeps them in order, to compare the estimate with reported usage.</summary>
    private sealed class RecordingClient : IChatClient
    {
        public IChatClient Inner { get; set; } = null!;

        public List<ChatRequest> Requests { get; } = [];

        public IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, CancellationToken cancellationToken)
        {
            lock (Requests)
            {
                Requests.Add(request);
            }

            return Inner.StreamAsync(request, apiKey, cancellationToken);
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken) =>
            Inner.ListModelsAsync(baseUrl, apiKey, cancellationToken);
    }
}
