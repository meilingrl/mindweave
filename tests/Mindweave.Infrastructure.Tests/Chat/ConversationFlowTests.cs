using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;

namespace Mindweave.Infrastructure.Tests.Chat;

public class ConversationFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_question_is_answered_persisted_and_titled()
    {
        await using var h = await ChatHarness.StartAsync();

        var sent = await h.Conversations.StartAsync(h.Workspace, "  火车是怎么发明的？ ", Ct);

        Assert.True(sent.Accepted);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        Assert.Equal(MessageState.Complete, answer.State);
        Assert.Equal("答：火车是怎么发明的？", answer.Body);
        Assert.Equal("completed", await h.TaskStateAsync(answer.Id));
        await ChatHarness.UntilAsync(() => h.Conversations.LoadAsync(sent.Conversation, Ct).Result!.Conversation.TitleSource == TitleSource.Auto);
        var content = await h.Conversations.LoadAsync(sent.Conversation, Ct);
        Assert.Equal(FakeChatClient.DefaultTitle, content!.Conversation.Title);
        Assert.Equal(["火车是怎么发明的？", "答：火车是怎么发明的？"], content.Messages.Select(m => m.Body));
    }

    [Fact]
    public async Task Follow_up_questions_send_the_history_in_order()
    {
        await using var h = await ChatHarness.StartAsync();
        var first = await h.Conversations.StartAsync(h.Workspace, "一", Ct);
        await h.WaitForAnswerAsync(first.Answer!.Value);

        var second = await h.Conversations.AskAsync(first.Conversation, "二", Ct);
        await h.WaitForAnswerAsync(second.Answer!.Value);

        var request = h.Client.Requests.Last(r => r.Turns[0].Role != ChatRole.System);
        Assert.Equal(["一", "答：一", "二"], request.Turns.Select(t => t.Content));
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.User], request.Turns.Select(t => t.Role));
    }

    [Fact]
    public async Task A_second_question_is_refused_while_the_answer_is_still_running()
    {
        await using var h = await ChatHarness.StartAsync();
        var held = h.Client.Hold("部分");
        var first = await h.Conversations.StartAsync(h.Workspace, "一", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var second = await h.Conversations.AskAsync(first.Conversation, "二", Ct);

        Assert.Equal(SendRejection.AnswerInProgress, second.Rejection);
        held.Finish();
    }

    [Fact]
    public async Task Stopping_a_running_answer_keeps_what_arrived()
    {
        await using var h = await ChatHarness.StartAsync();
        var held = h.Client.Hold("蒸汽机", "最早");
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        await h.Conversations.StopAsync(sent.Answer!.Value);

        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        Assert.Equal(MessageState.Stopped, answer.State);
        Assert.Equal("蒸汽机最早", answer.Body);
        Assert.Equal("stopped", await h.TaskStateAsync(answer.Id));
        Assert.Equal(MessageState.Stopped, h.Conversations.Observe(answer.Id)!.Snapshot().State);
    }

    [Fact]
    public async Task Only_two_requests_run_at_once_and_questions_go_before_titles()
    {
        await using var h = await ChatHarness.StartAsync();
        var holds = Enumerable.Range(0, 2).Select(_ => h.Client.Hold("…")).ToArray();
        var a = await h.Conversations.StartAsync(h.Workspace, "a", Ct);
        var b = await h.Conversations.StartAsync(h.Workspace, "b", Ct);
        await Task.WhenAll(holds.Select(x => x.Started.Task)).WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var c = await h.Conversations.StartAsync(h.Workspace, "c", Ct);
        await Task.Delay(150, Ct);

        Assert.Equal(2, h.Client.StartedAnswers);
        Assert.Equal(MessageState.Pending, (await h.GetMessageAsync(c.Answer!.Value))!.State);
        Assert.Equal(3, h.Generation.UnfinishedAnswerCount);

        // When a slot frees up, the waiting question runs before a's title task.
        holds[0].Finish();
        await h.WaitForAnswerAsync(c.Answer!.Value);
        var order = h.Client.Requests.Select(r => r.Turns[0].Role == ChatRole.System ? "title" : r.Turns[^1].Content).ToArray();
        Assert.Equal(["a", "b", "c"], order.Take(3));
        holds[1].Finish();
        await h.WaitForAnswerAsync(b.Answer!.Value);
        await h.WaitForAnswerAsync(a.Answer!.Value);
    }

    [Fact]
    public async Task A_queued_answer_can_be_cancelled_before_it_starts()
    {
        await using var h = await ChatHarness.StartAsync();
        var holds = Enumerable.Range(0, 2).Select(_ => h.Client.Hold("…")).ToArray();
        await h.Conversations.StartAsync(h.Workspace, "a", Ct);
        await h.Conversations.StartAsync(h.Workspace, "b", Ct);
        await Task.WhenAll(holds.Select(x => x.Started.Task)).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var c = await h.Conversations.StartAsync(h.Workspace, "c", Ct);

        await h.Conversations.StopAsync(c.Answer!.Value);

        var answer = await h.WaitForAnswerAsync(c.Answer!.Value);
        Assert.Equal(MessageState.Cancelled, answer.State);
        Assert.Equal("cancelled", await h.TaskStateAsync(answer.Id));
        foreach (var hold in holds)
        {
            hold.Finish();
        }

        await Task.Delay(100, Ct);
        Assert.Equal(2, h.Client.StartedAnswers);
    }

    [Fact]
    public async Task Retrying_a_failed_answer_keeps_the_failed_attempt_and_sends_clean_history()
    {
        await using var h = await ChatHarness.StartAsync();
        var first = await h.Conversations.StartAsync(h.Workspace, "一", Ct);
        await h.WaitForAnswerAsync(first.Answer!.Value);
        h.Client.Fail(GenerationErrorKind.ServerError, "HTTP 500: boom", "半截");
        var second = await h.Conversations.AskAsync(first.Conversation, "二", Ct);
        var failed = await h.WaitForAnswerAsync(second.Answer!.Value);
        Assert.Equal(MessageState.Failed, failed.State);
        Assert.Equal("半截", failed.Body);
        Assert.Equal(GenerationErrorKind.ServerError, failed.ErrorKind);

        var retry = await h.Conversations.RetryAsync(first.Conversation, failed.Id, Ct);
        var redone = await h.WaitForAnswerAsync(retry.Answer!.Value);

        Assert.Equal(failed.Id, redone.Supersedes);
        Assert.Equal("答：二", redone.Body);
        var request = h.Client.Requests.Last(r => r.Turns[0].Role != ChatRole.System);
        Assert.Equal(["一", "答：一", "二"], request.Turns.Select(t => t.Content));
        var messages = (await h.Conversations.LoadAsync(first.Conversation, Ct))!.Messages;
        Assert.Equal(5, messages.Count);
        Assert.Contains(messages, m => m.Id == failed.Id && m.Body == "半截");
    }

    [Fact]
    public async Task Only_the_last_answer_can_be_regenerated_and_the_newest_attempt_becomes_history()
    {
        await using var h = await ChatHarness.StartAsync();
        var first = await h.Conversations.StartAsync(h.Workspace, "一", Ct);
        var original = await h.WaitForAnswerAsync(first.Answer!.Value);

        h.Client.Reply("新答");
        var regen = await h.Conversations.RetryAsync(first.Conversation, original.Id, Ct);
        await h.WaitForAnswerAsync(regen.Answer!.Value);
        var notLast = await h.Conversations.RetryAsync(first.Conversation, original.Id, Ct);
        var next = await h.Conversations.AskAsync(first.Conversation, "二", Ct);
        await h.WaitForAnswerAsync(next.Answer!.Value);

        Assert.Equal(SendRejection.NotRetryable, notLast.Rejection);
        var request = h.Client.Requests.Last(r => r.Turns[0].Role != ChatRole.System);
        Assert.Equal(["一", "新答", "二"], request.Turns.Select(t => t.Content));
    }

    [Fact]
    public async Task A_manual_title_is_never_overwritten_by_automatic_titling()
    {
        await using var h = await ChatHarness.StartAsync();
        var held = h.Client.Hold("…");
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await h.Conversations.RenameAsync(sent.Conversation, "  我的  名字 ", Ct);

        held.Finish("完");
        await h.WaitForAnswerAsync(sent.Answer!.Value);
        await ChatHarness.UntilAsync(() => h.Generation.UnfinishedCount == 0);
        await Task.Delay(100, Ct);

        var conversation = (await h.Conversations.LoadAsync(sent.Conversation, Ct))!.Conversation;
        Assert.Equal("我的 名字", conversation.Title);
        Assert.Equal(TitleSource.Manual, conversation.TitleSource);
    }

    [Fact]
    public async Task Without_a_configured_model_questions_are_refused_and_nothing_is_stored()
    {
        await using var h = await ChatHarness.StartAsync(configure: false);

        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);

        Assert.Equal(SendRejection.NotConfigured, sent.Rejection);
        Assert.Empty(await h.Conversations.ListAsync(h.Workspace, Ct));
    }

    [Fact]
    public async Task A_locked_credential_store_fails_the_request_but_reading_still_works()
    {
        await using var h = await ChatHarness.StartAsync();
        var first = await h.Conversations.StartAsync(h.Workspace, "一", Ct);
        await h.WaitForAnswerAsync(first.Answer!.Value);
        h.Credentials.Unavailable = true;

        var second = await h.Conversations.AskAsync(first.Conversation, "二", Ct);
        var answer = await h.WaitForAnswerAsync(second.Answer!.Value);

        Assert.Equal(MessageState.Failed, answer.State);
        Assert.Equal(GenerationErrorKind.CredentialUnavailable, answer.ErrorKind);
        Assert.Equal(1, h.Client.StartedAnswers);
        var content = await h.Conversations.LoadAsync(first.Conversation, Ct);
        Assert.Equal("答：一", content!.Messages[1].Body);
    }

    [Fact]
    public async Task Partial_text_is_checkpointed_while_streaming()
    {
        await using var h = await ChatHarness.StartAsync();
        var held = h.Client.Hold("第一段", "第二段");
        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var streaming = await h.WaitForAnswerAsync(sent.Answer!.Value, m => m.Body == "第一段第二段");

        Assert.Equal(MessageState.Streaming, streaming.State);
        held.Finish("。");
        var done = await h.WaitForAnswerAsync(sent.Answer!.Value);
        Assert.Equal("第一段第二段。", done.Body);
        Assert.True(done.Revision > streaming.Revision);
    }

    [Fact]
    public async Task Shutdown_stops_running_answers_cancels_queued_ones_and_refuses_new_work()
    {
        await using var h = await ChatHarness.StartAsync();
        var holds = Enumerable.Range(0, 2).Select(_ => h.Client.Hold("已收到")).ToArray();
        var a = await h.Conversations.StartAsync(h.Workspace, "a", Ct);
        var b = await h.Conversations.StartAsync(h.Workspace, "b", Ct);
        await Task.WhenAll(holds.Select(x => x.Started.Task)).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var c = await h.Conversations.StartAsync(h.Workspace, "c", Ct);

        await h.Generation.ShutdownAsync();

        Assert.Equal(MessageState.Stopped, (await h.GetMessageAsync(a.Answer!.Value))!.State);
        Assert.Equal("已收到", (await h.GetMessageAsync(b.Answer!.Value))!.Body);
        Assert.Equal(MessageState.Cancelled, (await h.GetMessageAsync(c.Answer!.Value))!.State);
        Assert.Equal(0, h.Generation.UnfinishedCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Conversations.StartAsync(h.Workspace, "d", Ct));
    }

    [Fact]
    public async Task A_failed_terminal_write_blocks_new_requests_until_it_is_saved()
    {
        FlakyStore? flaky = null;
        await using var h = await ChatHarness.StartAsync(wrapStore: s => flaky = new FlakyStore(s));
        flaky!.FailFinishes = 2;

        var sent = await h.Conversations.StartAsync(h.Workspace, "问", Ct);
        await ChatHarness.UntilAsync(() => h.Generation.IsStorageBlocked);

        var refused = await h.Conversations.StartAsync(h.Workspace, "再问", Ct);
        Assert.Equal(SendRejection.StorageUnavailable, refused.Rejection);
        Assert.Equal("答：问", h.Conversations.Observe(sent.Answer!.Value)!.Snapshot().Text);

        var saved = await h.WaitForAnswerAsync(sent.Answer!.Value);
        Assert.Equal(MessageState.Complete, saved.State);
        await ChatHarness.UntilAsync(() => !h.Generation.IsStorageBlocked);
    }

    [Fact]
    public async Task Restart_marks_unfinished_answers_interrupted_without_sending_anything()
    {
        using var dir = new TempDataDirectory();
        MessageId answer;
        await using (var before = await ChatHarness.StartAsync(directory: dir))
        {
            var held = before.Client.Hold("说到一半");
            var sent = await before.Conversations.StartAsync(before.Workspace, "问", Ct);
            answer = sent.Answer!.Value;
            await before.WaitForAnswerAsync(answer, m => m.Body == "说到一半");

            // Simulate the process dying: no shutdown, just drop the database connection.
            await before.Database.CloseAsync();
            held.Finish();
        }

        await using var after = await ChatHarness.StartAsync(directory: dir, configure: false);
        var report = await after.Store.RecoverUnfinishedAsync(DateTimeOffset.UtcNow, Ct);

        Assert.Equal(1, report.InterruptedAnswers);
        var recovered = await after.GetMessageAsync(answer);
        Assert.Equal(MessageState.Interrupted, recovered!.State);
        Assert.Equal("说到一半", recovered.Body);
        Assert.Equal("interrupted", await after.TaskStateAsync(answer));
        Assert.Empty(after.Client.Requests);
    }

    /// <summary>Wraps the real store and fails the first N terminal answer writes.</summary>
    private sealed class FlakyStore(IConversationStore inner) : IConversationStore
    {
        private int _failFinishes;

        public int FailFinishes
        {
            get => Volatile.Read(ref _failFinishes);
            set => Volatile.Write(ref _failFinishes, value);
        }

        public Task<bool> FinishAnswerAsync(GenerationTaskId task, MessageId answer, string body, long revision, MessageState state, GenerationErrorKind? errorKind, string? errorDetail, DateTimeOffset at, CancellationToken cancellationToken) =>
            Interlocked.Decrement(ref _failFinishes) >= 0
                ? throw new IOException("simulated disk full")
                : inner.FinishAnswerAsync(task, answer, body, revision, state, errorKind, errorDetail, at, cancellationToken);

        public Task<IReadOnlyList<ConversationSummary>> ListAsync(Core.Workspaces.WorkspaceId workspace, CancellationToken cancellationToken) => inner.ListAsync(workspace, cancellationToken);

        public Task<Conversation?> GetAsync(ConversationId id, CancellationToken cancellationToken) => inner.GetAsync(id, cancellationToken);

        public Task<IReadOnlyList<Message>> GetMessagesAsync(ConversationId id, CancellationToken cancellationToken) => inner.GetMessagesAsync(id, cancellationToken);

        public Task CreateAsync(Conversation conversation, Message question, Message answer, GenerationTaskRecord task, CancellationToken cancellationToken) => inner.CreateAsync(conversation, question, answer, task, cancellationToken);

        public Task<(Message Question, Message Answer)> AppendQuestionAsync(Message question, Message answer, GenerationTaskRecord task, CancellationToken cancellationToken) => inner.AppendQuestionAsync(question, answer, task, cancellationToken);

        public Task<Message> AddRetryAsync(Message answer, GenerationTaskRecord task, CancellationToken cancellationToken) => inner.AddRetryAsync(answer, task, cancellationToken);

        public Task AddTaskAsync(GenerationTaskRecord task, CancellationToken cancellationToken) => inner.AddTaskAsync(task, cancellationToken);

        public Task<bool> MarkRunningAsync(GenerationTaskId task, MessageId? answer, DateTimeOffset at, CancellationToken cancellationToken) => inner.MarkRunningAsync(task, answer, at, cancellationToken);

        public Task<bool> CheckpointAsync(MessageId answer, string body, long revision, CancellationToken cancellationToken) => inner.CheckpointAsync(answer, body, revision, cancellationToken);

        public Task FinishTaskAsync(GenerationTaskId task, GenerationTaskState state, GenerationErrorKind? errorKind, string? errorDetail, DateTimeOffset at, CancellationToken cancellationToken) => inner.FinishTaskAsync(task, state, errorKind, errorDetail, at, cancellationToken);

        public Task<bool> SetAutoTitleAsync(ConversationId id, string title, DateTimeOffset at, CancellationToken cancellationToken) => inner.SetAutoTitleAsync(id, title, at, cancellationToken);

        public Task SetManualTitleAsync(ConversationId id, string title, DateTimeOffset at, CancellationToken cancellationToken) => inner.SetManualTitleAsync(id, title, at, cancellationToken);

        public Task<RecoveryReport> RecoverUnfinishedAsync(DateTimeOffset at, CancellationToken cancellationToken) => inner.RecoverUnfinishedAsync(at, cancellationToken);

        public Task<IReadOnlySet<string>> CredentialRefsInUseAsync(CancellationToken cancellationToken) => inner.CredentialRefsInUseAsync(cancellationToken);
    }
}
