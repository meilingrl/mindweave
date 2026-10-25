using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>The database-level guards that keep late writes from rewriting history.</summary>
public class StoreGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Checkpoints_never_roll_back_and_never_touch_a_finished_answer()
    {
        await using var h = await ChatHarness.StartAsync();
        var (task, answer) = await SeedStreamingAsync(h);

        Assert.True(await h.Store.CheckpointAsync(answer, "abc", 3, Ct));
        Assert.False(await h.Store.CheckpointAsync(answer, "a", 2, Ct));
        Assert.Equal("abc", (await h.GetMessageAsync(answer))!.Body);

        Assert.True(await h.Store.FinishAnswerAsync(task, answer, "abcd", 4, MessageState.Complete, null, null, DateTimeOffset.UtcNow, Ct));
        Assert.False(await h.Store.CheckpointAsync(answer, "abcde-late", 99, Ct));
        Assert.False(await h.Store.FinishAnswerAsync(task, answer, "other", 100, MessageState.Failed, GenerationErrorKind.Network, null, DateTimeOffset.UtcNow, Ct));

        var stored = await h.GetMessageAsync(answer);
        Assert.Equal("abcd", stored!.Body);
        Assert.Equal(MessageState.Complete, stored.State);
    }

    [Fact]
    public async Task Appending_while_an_answer_is_unfinished_is_rejected_inside_the_transaction()
    {
        await using var h = await ChatHarness.StartAsync();
        var (_, answer) = await SeedStreamingAsync(h);
        var conversation = (await h.GetMessageAsync(answer))!.ConversationId;
        var now = DateTimeOffset.UtcNow;
        var question = new Message(MessageId.New(), conversation, 0, MessageRole.User, "q", MessageState.Complete, 0, null, null, null, now, now);
        var pending = new Message(MessageId.New(), conversation, 0, MessageRole.Assistant, string.Empty, MessageState.Pending, 0, null, null, null, now, null);

        await Assert.ThrowsAsync<AnswerInProgressException>(() => h.Store.AppendQuestionAsync(question, pending, Task(conversation, pending.Id), Ct));
    }

    [Fact]
    public async Task Task_rows_keep_the_snapshot_they_were_accepted_with()
    {
        await using var h = await ChatHarness.StartAsync();
        var sent = await h.Conversations.StartAsync(h.Workspace, "q", Ct);
        await h.WaitForAnswerAsync(sent.Answer!.Value);

        await h.Models.SaveAsync(new Application.Models.ModelSettingsInput("x", "https://other.example/v1", "other-model", "sk-new"), Ct);

        var row = await h.Database.ReadAsync(
            c =>
            {
                using var cmd = c.Command(null, "SELECT base_url, model FROM generation_task WHERE target_message_id = $id;", ("$id", sent.Answer!.Value.ToString()));
                using var r = cmd.ExecuteReader();
                r.Read();
                return (r.GetString(0), r.GetString(1));
            },
            Ct);
        Assert.Equal(("https://fake.example/v1", "fake-model"), row);
    }

    private static async Task<(GenerationTaskId Task, MessageId Answer)> SeedStreamingAsync(ChatHarness h)
    {
        var held = h.Client.Hold("x");
        var sent = await h.Conversations.StartAsync(h.Workspace, "q", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await h.Generation.StopAsync(sent.Answer!.Value);

        // Re-create a streaming row directly so the guards can be exercised without the coordinator.
        var conversation = sent.Conversation;
        var now = DateTimeOffset.UtcNow;
        var answer = new Message(MessageId.New(), conversation, 0, MessageRole.Assistant, string.Empty, MessageState.Pending, 0, null, null, null, now, null);
        var question = new Message(MessageId.New(), conversation, 0, MessageRole.User, "q2", MessageState.Complete, 0, null, null, null, now, now);
        var task = Task(conversation, answer.Id);
        await h.Store.AppendQuestionAsync(question, answer, task, Ct);
        Assert.True(await h.Store.MarkRunningAsync(task.Id, answer.Id, now, Ct));
        return (task.Id, answer.Id);
    }

    private static GenerationTaskRecord Task(ConversationId conversation, MessageId answer) =>
        new(GenerationTaskId.New(), GenerationTaskKind.Answer, conversation, answer,
            new ModelSnapshot("m", "https://fake.example/v1", ChatProtocol.ChatCompletions, "fake-model", "ref", null),
            [], DateTimeOffset.UtcNow);
}
