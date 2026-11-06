using System.Runtime.CompilerServices;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Diagnostics;
using Mindweave.Core.Conversations;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Reasoning models send their reasoning long before any answer text (2026-11-05). The answer says it is thinking; the
/// reasoning itself is neither shown nor stored, and requests whose output the program uses ignore it.
/// </summary>
public class ThinkingPhaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_reasoning_model_is_thinking_until_its_text_arrives()
    {
        await using var h = await ChatHarness.StartAsync();
        var held = h.Client.HoldThinking();

        var sent = await h.Conversations.StartAsync(h.Workspace, "火车", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var live = h.Conversations.Observe(sent.Answer!.Value)!;

        await ChatHarness.UntilAsync(() => live.Snapshot().Phase == AnswerPhase.Thinking);
        Assert.Equal(string.Empty, live.Snapshot().Text);
        Assert.Equal(MessageState.Streaming, live.Snapshot().State);

        held.Finish("蒸汽机车");
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);
        Assert.Equal(MessageState.Complete, answer.State);
        Assert.Equal("蒸汽机车", answer.Text);
    }

    [Fact]
    public async Task Stopping_while_thinking_stops_with_no_text()
    {
        await using var h = await ChatHarness.StartAsync();
        var held = h.Client.HoldThinking();
        var sent = await h.Conversations.StartAsync(h.Workspace, "火车", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await ChatHarness.UntilAsync(() => h.Conversations.Observe(sent.Answer!.Value)?.Snapshot().Phase == AnswerPhase.Thinking);

        await h.Conversations.StopAsync(sent.Answer!.Value);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(MessageState.Stopped, answer.State);
        Assert.Equal(string.Empty, answer.Text);
        var record = Assert.Single(await RequestsForAsync(h, answer.Id));
        Assert.Equal(AiRequestOutcome.Stopped, record.Outcome);
        Assert.Equal(0, record.OutputCharacters);
        Assert.Null(record.TimeToFirstText);
    }

    [Fact]
    public async Task Reasoning_after_text_has_arrived_does_not_change_the_phase()
    {
        var client = new TextThenReasoningClient();
        await using var h = await ChatHarness.StartAsync(client: client);

        var sent = await h.Conversations.StartAsync(h.Workspace, "火车", Ct);
        await client.Reasoned.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var live = h.Conversations.Observe(sent.Answer!.Value)!;

        Assert.Equal(AnswerPhase.Waiting, live.Snapshot().Phase);
        Assert.Equal("部分", live.Snapshot().Text);
        client.Release.TrySetResult();
        Assert.Equal(MessageState.Complete, (await h.WaitForAnswerAsync(sent.Answer!.Value)).State);
    }

    [Fact]
    public async Task Titles_knowledge_summaries_and_traces_ignore_reasoning()
    {
        await using var h = await ChatHarness.StartAsync();
        h.Client.ReasonFirst = true;

        var sent = await h.Conversations.StartAsync(h.Workspace, "火车", Ct);
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal("答：火车", answer.Text);
        Assert.Equal("答：火车".Length, Assert.Single(await RequestsForAsync(h, answer.Id)).OutputCharacters);
        await ChatHarness.UntilAsync(() => h.Conversations.LoadAsync(sent.Conversation, Ct).Result!.Conversation.TitleSource == TitleSource.Auto);
        Assert.Equal(FakeChatClient.DefaultTitle, (await h.Conversations.LoadAsync(sent.Conversation, Ct))!.Conversation.Title);

        Assert.True((await h.SummaryService.SummarizeAsync(sent.Conversation, Ct)).Accepted);
        await ChatHarness.UntilAsync(() => h.SummaryService.LatestTaskAsync(sent.Conversation, Ct).Result is { State: not (Application.Storage.GenerationTaskState.Queued or Application.Storage.GenerationTaskState.Running) });
        Assert.Equal(Application.Storage.GenerationTaskState.Completed, (await h.SummaryService.LatestTaskAsync(sent.Conversation, Ct))!.State);
        Assert.Equal(FakeChatClient.DefaultSummaryTitle, (await h.SummaryService.ForConversationAsync(sent.Conversation, Ct))!.Current.Title);
    }

    [Fact]
    public async Task Hundreds_of_reasoning_chunks_notify_the_answer_once()
    {
        var client = new GatedReasoningClient(chunks: 200);
        await using var h = await ChatHarness.StartAsync(client: client);

        var sent = await h.Conversations.StartAsync(h.Workspace, "火车", Ct);
        await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var live = h.Conversations.Observe(sent.Answer!.Value)!;
        var changes = 0;
        live.Changed += (_, _) => Interlocked.Increment(ref changes);
        client.Go.TrySetResult();
        await client.Reasoned.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // The view re-reads the answer on every change; reasoning only needs to say once that the model is thinking.
        Assert.Equal(AnswerPhase.Thinking, live.Snapshot().Phase);
        Assert.Equal(1, Volatile.Read(ref changes));
        client.Release.TrySetResult();
        Assert.Equal("答", (await h.WaitForAnswerAsync(sent.Answer!.Value)).Text);
    }

    /// <summary>The trace is written after the answer's terminal state, so wait for it.</summary>
    private static async Task<IReadOnlyList<AiRequestRecord>> RequestsForAsync(ChatHarness h, MessageId answer)
    {
        var task = await h.Database.ReadAsync(
            c =>
            {
                using var cmd = c.Command(null, "SELECT id FROM generation_task WHERE target_message_id = $id;", ("$id", answer.ToString()));
                return GenerationTaskId.Parse((string)cmd.ExecuteScalar()!);
            },
            Ct);
        IReadOnlyList<AiRequestRecord> records = [];
        await ChatHarness.UntilAsync(() => (records = h.Requests.ForTaskAsync(task, Ct).Result).Count > 0);
        return records;
    }

    /// <summary>
    /// Once the answer's stream has started, waits for <see cref="Go"/>, reasons in many chunks, then waits for
    /// <see cref="Release"/> before its text; titles get a plain reply.
    /// </summary>
    private sealed class GatedReasoningClient(int chunks) : IChatClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Go { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Reasoned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (!FakeChatClient.IsAnswer(request))
            {
                yield return new ChatStreamEvent.TextDelta("标题");
                yield return new ChatStreamEvent.Completed(false);
                yield break;
            }

            Entered.TrySetResult();
            await Go.Task.WaitAsync(cancellationToken);
            for (var i = 0; i < chunks; i++)
            {
                yield return new ChatStreamEvent.ReasoningDelta(4);
            }

            // The coordinator has handled the last reasoning chunk once the next event is asked for.
            Reasoned.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            yield return new ChatStreamEvent.TextDelta("答");
            yield return new ChatStreamEvent.Completed(false);
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["fake-model"]);
    }

    /// <summary>Answers with some text, then reasons (some models interleave), then waits; titles get a plain reply.</summary>
    private sealed class TextThenReasoningClient : IChatClient
    {
        public TaskCompletionSource Reasoned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ChatStreamEvent> StreamAsync(ChatRequest request, string apiKey, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (!FakeChatClient.IsAnswer(request))
            {
                yield return new ChatStreamEvent.TextDelta("标题");
                yield return new ChatStreamEvent.Completed(false);
                yield break;
            }

            yield return new ChatStreamEvent.TextDelta("部分");
            yield return new ChatStreamEvent.ReasoningDelta(10);

            // The coordinator has handled the reasoning once the next event is asked for.
            Reasoned.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            yield return new ChatStreamEvent.Completed(false);
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["fake-model"]);
    }
}
