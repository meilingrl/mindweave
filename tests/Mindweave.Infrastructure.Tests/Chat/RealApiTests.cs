using Mindweave.Application.Models;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Ai;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Handoff S1 acceptance against a real service: long streamed answer, stop mid-stream, multi-turn context,
/// automatic title. Opt-in through scripts/real-api.sh, which supplies the key via the environment only.
/// A full run makes about ten short requests.
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

    private static async Task<ChatHarness> StartAsync()
    {
        Assert.SkipWhen(Key is null || Url is null || Model is null, "Run through scripts/real-api.sh test.");
        var http = new HttpClient(ChatCompletionsClient.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        return await ChatHarness.StartAsync(
            client: new ChatCompletionsClient(http, new ChatClientOptions()),
            model: new ModelSettingsInput("real", Url!, Model!, Key!));
    }
}
