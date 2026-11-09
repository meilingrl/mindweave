using Mindweave.Application.Storage;
using Mindweave.Core.Conversations;
using Mindweave.Core.Summaries;
using Mindweave.Infrastructure.Sqlite;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Common conversation steps on a <see cref="ChatHarness"/>, each waiting until its result is stored. Shared by the
/// Chat and Faults suites instead of a private copy in every file.
/// </summary>
internal static class ChatFlow
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Starts a conversation with <paramref name="question"/> and waits for its finished answer.</summary>
    public static async Task<(ConversationId Root, Message Answer)> RootWithAnswerAsync(this ChatHarness h, string question)
    {
        var sent = await h.Conversations.StartAsync(h.Workspace, question, Ct);
        return (sent.Conversation, await h.WaitForAnswerAsync(sent.Answer!.Value));
    }

    /// <summary>Asks <paramref name="question"/> in <paramref name="conversation"/> and waits for its finished answer.</summary>
    public static async Task<Message> AskAsync(this ChatHarness h, ConversationId conversation, string question)
    {
        var sent = await h.Conversations.AskAsync(conversation, question, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        return await h.WaitForAnswerAsync(sent.Answer!.Value);
    }

    /// <summary>Branches from <paramref name="answer"/> quoting <paramref name="quote"/>; returns the child conversation.</summary>
    public static async Task<ConversationId> BranchAsync(this ChatHarness h, ConversationId parent, Message answer, string quote = "q")
    {
        var branch = await h.Conversations.CreateBranchAsync(parent, BranchFlowTests.Selection(answer, quote), Ct);
        Assert.True(branch.Accepted, branch.Rejection?.ToString());
        return branch.Child;
    }

    /// <summary>Retries <paramref name="answer"/> once the conversation has no unfinished answer, and waits for the new one.</summary>
    public static async Task<Message> RetryAsync(this ChatHarness h, ConversationId conversation, MessageId answer)
    {
        await ChatHarness.UntilAsync(() => !h.Generation.HasUnfinishedAnswer(conversation));
        var sent = await h.Conversations.RetryAsync(conversation, answer, Ct);
        Assert.True(sent.Accepted, sent.Rejection?.ToString());
        return await h.WaitForAnswerAsync(sent.Answer!.Value);
    }

    /// <summary>Summarizes <paramref name="conversation"/> and waits until that task completed; returns the summary node.</summary>
    public static async Task<SummaryNodeId> SummarizeAsync(this ChatHarness h, ConversationId conversation)
    {
        var outcome = await h.SummaryService.SummarizeAsync(conversation, Ct);
        Assert.True(outcome.Accepted, outcome.Rejection?.ToString());
        await ChatHarness.UntilAsync(async () =>
            await h.SummaryService.LatestTaskAsync(conversation, Ct) is { State: GenerationTaskState.Completed } s && s.Id == outcome.Task);
        return (await h.SummaryService.ForConversationAsync(conversation, Ct))!.Node.Id;
    }

    /// <summary>The number of rows in <paramref name="table"/>.</summary>
    public static Task<long> CountAsync(this ChatHarness h, string table) =>
        h.Database.ReadAsync(c => c.Scalar<long>(null, $"SELECT count(*) FROM {table};"), Ct);
}
