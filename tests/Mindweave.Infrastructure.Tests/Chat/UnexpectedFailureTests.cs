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

    /// <summary>Health check 2026-11-12, item 5: a cut-off answer keeps its text with an incomplete mark and its reason.</summary>
    [Fact]
    public async Task An_answer_cut_off_at_the_output_limit_is_kept_as_stopped_with_the_reason()
    {
        await using var h = await ChatHarness.StartAsync();
        h.Client.CutOff("蒸汽机车的发明始于十八世纪，其中最重要的");

        var sent = await h.Conversations.StartAsync(h.Workspace, "详细讲讲火车的历史", Ct);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(MessageState.Stopped, answer.State);
        Assert.Equal(GenerationErrorKind.OutputLimit, answer.ErrorKind);
        Assert.Equal("蒸汽机车的发明始于十八世纪，其中最重要的", answer.Text);
        Assert.Equal("stopped", await TaskStateAsync(h, "answer", answer.Id));
    }

    [Fact]
    public async Task An_answer_stopped_by_the_content_filter_says_so()
    {
        await using var h = await ChatHarness.StartAsync();
        h.Client.Filtered("开头的部分");

        var sent = await h.Conversations.StartAsync(h.Workspace, "问题", Ct);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(MessageState.Stopped, answer.State);
        Assert.Equal(GenerationErrorKind.ContentFiltered, answer.ErrorKind);
    }

    [Fact]
    public async Task An_answer_that_ends_without_any_text_fails_and_can_be_retried()
    {
        await using var h = await ChatHarness.StartAsync();
        h.Client.CutOff();

        var sent = await h.Conversations.StartAsync(h.Workspace, "问题", Ct);
        var empty = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(MessageState.Failed, empty.State);
        Assert.Equal(GenerationErrorKind.OutputLimit, empty.ErrorKind);
        var retry = await h.Conversations.RetryAsync(sent.Conversation, empty.Id, Ct);
        Assert.Equal("答：问题", (await h.WaitForAnswerAsync(retry.Answer!.Value)).Text);
    }

    [Fact]
    public async Task A_finished_answer_without_any_text_fails()
    {
        await using var h = await ChatHarness.StartAsync();
        h.Client.Reply();

        var sent = await h.Conversations.StartAsync(h.Workspace, "问题", Ct);
        var empty = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(MessageState.Failed, empty.State);
        Assert.Equal(GenerationErrorKind.InvalidResponse, empty.ErrorKind);
    }

    /// <summary>
    /// Health check 2026-11-12, gap-lifecycle-1: a summary task's final write refused by storage is kept and written
    /// once storage takes writes again, as an answer's is, instead of leaving the task "running" until a restart.
    /// </summary>
    [Fact]
    public async Task A_summary_tasks_final_write_refused_by_storage_is_kept_and_written_later()
    {
        var refusing = new RefuseOnce();
        await using var h = await ChatHarness.StartAsync(wrapStore: store => RefuseOnce.Wrap(store, refusing));
        var sent = await h.Conversations.StartAsync(h.Workspace, "一", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        await h.SettleAsync(sent.Conversation);
        h.Client.Knowledge.FailNext(GenerationErrorKind.ServerError);
        refusing.Armed = true;

        var outcome = await h.SummaryService.SummarizeAsync(sent.Conversation, Ct);

        Assert.True(outcome.Accepted, outcome.Rejection?.ToString());
        await ChatHarness.UntilAsync(() => Task.FromResult(refusing.Refused));
        await ChatHarness.UntilAsync(async () => await h.SummaryService.LatestTaskAsync(sent.Conversation, Ct) is { State: GenerationTaskState.Failed });
        await ChatHarness.UntilAsync(() => Task.FromResult(!h.Health.IsBlocked));
    }

    /// <summary>A conversation store whose first task-finishing write after arming is refused as on a full disk.</summary>
    public class RefuseOnce : System.Reflection.DispatchProxy
    {
        private IConversationStore _inner = null!;
        private RefuseOnce _gate = null!;

        private volatile bool _armed;
        private volatile bool _refused;

        public bool Armed { get => _armed; set => _armed = value; }

        public bool Refused { get => _refused; set => _refused = value; }

        public static IConversationStore Wrap(IConversationStore inner, RefuseOnce gate)
        {
            var proxy = Create<IConversationStore, RefuseOnce>();
            ((RefuseOnce)(object)proxy)._inner = inner;
            ((RefuseOnce)(object)proxy)._gate = gate;
            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IConversationStore.FinishTaskAsync) && _gate.Armed && !_gate.Refused)
            {
                _gate.Refused = true;
                return Task.FromException(new StorageWriteException(StorageFailureKind.Full, "simulated full disk", null));
            }

            return targetMethod.Invoke(_inner, args);
        }
    }

    /// <summary>Health check 2026-11-12, tests-1: a stream that ends without the service finishing the answer fails it, keeping the text.</summary>
    [Fact]
    public async Task An_answer_whose_stream_ends_without_finishing_fails_and_keeps_its_text()
    {
        await using var h = await ChatHarness.StartAsync();
        h.Client.EndWithoutFinishing("说到一半");

        var sent = await h.Conversations.StartAsync(h.Workspace, "问题", Ct);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(MessageState.Failed, answer.State);
        Assert.Equal(GenerationErrorKind.InvalidResponse, answer.ErrorKind);
        Assert.Equal("说到一半", answer.Text);
        Assert.Contains("without finishing", answer.ErrorDetail, StringComparison.Ordinal);
    }

    /// <summary>Health check 2026-11-12, tests-2: an internal request answered with nothing is not used.</summary>
    [Fact]
    public async Task A_title_answered_with_nothing_fails_its_task_and_keeps_no_title()
    {
        await using var h = await ChatHarness.StartAsync();
        h.Client.Titles.Replies.Enqueue("   ");

        var sent = await h.Conversations.StartAsync(h.Workspace, "一", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);

        await ChatHarness.UntilAsync(async () => await KindStateAsync(h, "title", sent.Conversation) == "failed");
    }

    private static Task<string?> TaskStateAsync(ChatHarness h, string kind, MessageId message) =>
        h.Database.ReadAsync(c => c.Scalar<string?>(null, "SELECT state FROM generation_task WHERE kind = $kind AND target_message_id = $m;", ("$kind", kind), ("$m", message.ToString())), Ct);

    private static Task<string?> KindStateAsync(ChatHarness h, string kind, ConversationId conversation) =>
        h.Database.ReadAsync(c => c.Scalar<string?>(null, "SELECT state FROM generation_task WHERE kind = $kind AND conversation_id = $id ORDER BY created_at DESC LIMIT 1;", ("$kind", kind), ("$id", conversation.ToString())), Ct);
}
