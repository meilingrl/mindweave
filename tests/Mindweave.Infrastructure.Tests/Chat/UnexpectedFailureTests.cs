using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Health check 2026-11-12, item 2: a run that ends in an exception nobody foresaw (a defect, an unforeseen response
/// shape) still ends as a failure — the answer and the task row are finished, the conversation takes new questions, and
/// the request is recorded — instead of staying "streaming" until the next start.
/// </summary>
public class UnexpectedFailureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_crashed_answer_fails_keeps_its_text_and_frees_the_conversation()
    {
        await using var h = await ChatHarness.StartAsync();
        var first = await h.Conversations.StartAsync(h.Workspace, "一", Ct);
        await h.WaitForAnswerAsync(first.Answer!.Value);
        var requests = await h.CountAsync("ai_request");
        h.Client.Crash(new InvalidOperationException("boom"), "半截");

        var second = await h.Conversations.AskAsync(first.Conversation, "二", Ct);
        var failed = await h.WaitForAnswerAsync(second.Answer!.Value);

        Assert.Equal(MessageState.Failed, failed.State);
        Assert.Equal(GenerationErrorKind.InvalidResponse, failed.ErrorKind);
        Assert.Equal("半截", failed.Text);
        Assert.Contains("boom", failed.ErrorDetail, StringComparison.Ordinal);
        Assert.Equal("failed", await TaskStateAsync(h, "answer", failed.Id));
        await ChatHarness.UntilAsync(async () => await h.CountAsync("ai_request") > requests);

        var retry = await h.Conversations.RetryAsync(first.Conversation, failed.Id, Ct);
        Assert.Equal("答：二", (await h.WaitForAnswerAsync(retry.Answer!.Value)).Text);
    }

    [Fact]
    public async Task A_crashed_title_request_fails_its_task()
    {
        await using var h = await ChatHarness.StartAsync();
        h.Client.Titles.CrashNext(new InvalidOperationException("boom"));

        var sent = await h.Conversations.StartAsync(h.Workspace, "一", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);

        await ChatHarness.UntilAsync(async () => await KindStateAsync(h, "title", sent.Conversation) == "failed");
    }

    [Fact]
    public async Task A_crashed_summary_request_fails_its_task()
    {
        await using var h = await ChatHarness.StartAsync();
        var sent = await h.Conversations.StartAsync(h.Workspace, "一", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        await h.SettleAsync(sent.Conversation);
        h.Client.Knowledge.CrashNext(new InvalidOperationException("boom"));

        var outcome = await h.SummaryService.SummarizeAsync(sent.Conversation, Ct);

        Assert.True(outcome.Accepted, outcome.Rejection?.ToString());
        await ChatHarness.UntilAsync(async () => await h.SummaryService.LatestTaskAsync(sent.Conversation, Ct) is { State: GenerationTaskState.Failed });
    }

    private static Task<string?> TaskStateAsync(ChatHarness h, string kind, MessageId message) =>
        h.Database.ReadAsync(c => c.Scalar<string?>(null, "SELECT state FROM generation_task WHERE kind = $kind AND target_message_id = $m;", ("$kind", kind), ("$m", message.ToString())), Ct);

    private static Task<string?> KindStateAsync(ChatHarness h, string kind, ConversationId conversation) =>
        h.Database.ReadAsync(c => c.Scalar<string?>(null, "SELECT state FROM generation_task WHERE kind = $kind AND conversation_id = $id ORDER BY created_at DESC LIMIT 1;", ("$kind", kind), ("$id", conversation.ToString())), Ct);
}
