using System.Text;
using Mindweave.Application.Storage;
using Mindweave.Core.Branches;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Workspaces;

namespace Mindweave.Infrastructure.Tests.Performance;

/// <summary>Shape of a generated workspace. Same shape, same data: content is derived from indexes only.</summary>
internal sealed record PerfShape(int Conversations, int TurnsInLongConversation, int AnswerCharacters, int BranchDepth, int TurnsPerBranch)
{
    /// <summary>Small enough to run on every test pass, so the generator never rots.</summary>
    public static PerfShape Smoke { get; } = new(20, 10, 2_000, 3, 2);

    /// <summary>The budgeted size (docs/evolution.md E6): a heavy year of use, with a ten-level branch chain.</summary>
    public static PerfShape Full { get; } = new(1_000, 200, 20_000, 10, 20);
}

/// <summary>
/// Fills a workspace through the real store, so the data passes the same guards and schema as production
/// writes: many one-turn conversations, one long conversation with long Markdown answers, and a chain of
/// branches, each created from the last answer of the previous level.
/// </summary>
internal static class PerfSeed
{
    private static readonly ModelSnapshot Model = new("perf", "https://perf.example/v1", ChatProtocol.ChatCompletions, "perf-model", "perf/key", null, ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions));

    public static async Task<ConversationId> FillAsync(IConversationStore store, WorkspaceId workspace, PerfShape shape, CancellationToken cancellationToken)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var answer = Answer(shape.AnswerCharacters);
        for (var i = 1; i < shape.Conversations; i++)
        {
            await StartAsync(store, workspace, start.AddMinutes(i), $"问题 {i}：蒸汽机如何改变了交通？", answer, cancellationToken);
        }

        var at = start.AddMinutes(shape.Conversations);
        var conversation = await StartAsync(store, workspace, at, "长对话的第一个问题", answer, cancellationToken);
        for (var turn = 1; turn < shape.TurnsInLongConversation; turn++)
        {
            at = at.AddSeconds(1);
            await AppendTurnAsync(store, conversation, $"追问 {turn}", answer, at, cancellationToken);
        }

        return conversation;
    }

    /// <summary>A chain of <see cref="PerfShape.BranchDepth"/> branches below <paramref name="root"/>; returns the deepest.</summary>
    public static async Task<ConversationId> FillBranchChainAsync(IConversationStore store, IBranchStore branches, ConversationId root, PerfShape shape, CancellationToken cancellationToken)
    {
        var answer = Answer(shape.AnswerCharacters);
        var at = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var parent = root;
        for (var level = 0; level < shape.BranchDepth; level++)
        {
            var source = (await store.GetMessagesAsync(parent, cancellationToken).ConfigureAwait(false))[^1];
            var child = ConversationId.New();
            var selection = new BranchSelection(source.Id, 0, "蒸汽机", "{}", 1, "perf");
            await branches.CreateAsync(parent, child, selection, at, cancellationToken).ConfigureAwait(false);
            for (var turn = 0; turn < shape.TurnsPerBranch; turn++)
            {
                at = at.AddSeconds(1);
                await AppendTurnAsync(store, child, $"第 {level} 层追问 {turn}", answer, at, cancellationToken);
            }

            parent = child;
        }

        return parent;
    }

    private static async Task AppendTurnAsync(IConversationStore store, ConversationId conversation, string question, string answer, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var q = NewMessage(conversation, MessageRole.User, question, MessageState.Complete, at);
        var pending = NewMessage(conversation, MessageRole.Assistant, string.Empty, MessageState.Pending, at);
        var task = NewTask(conversation, pending.Id, at);
        await store.AppendQuestionAsync(q, pending, task, cancellationToken).ConfigureAwait(false);
        await CompleteAsync(store, task, pending.Id, answer, at, cancellationToken);
    }

    private static async Task<ConversationId> StartAsync(IConversationStore store, WorkspaceId workspace, DateTimeOffset at, string question, string answer, CancellationToken cancellationToken)
    {
        var conversation = new Conversation(ConversationId.New(), workspace, null, TitleSource.None, at, at);
        var q = NewMessage(conversation.Id, MessageRole.User, question, MessageState.Complete, at);
        var a = NewMessage(conversation.Id, MessageRole.Assistant, string.Empty, MessageState.Pending, at);
        var task = NewTask(conversation.Id, a.Id, at);
        await store.CreateAsync(conversation, q, a, task, null, cancellationToken).ConfigureAwait(false);
        await CompleteAsync(store, task, a.Id, answer, at, cancellationToken);
        return conversation.Id;
    }

    private static async Task CompleteAsync(IConversationStore store, GenerationTaskRecord task, MessageId answer, string text, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await store.MarkRunningAsync(task.Id, answer, at, cancellationToken).ConfigureAwait(false);
        await store.FinishAnswerAsync(task.Id, answer, ContentBlocks.FromText(text), 1, MessageState.Complete, null, null, at, cancellationToken).ConfigureAwait(false);
    }

    private static Message NewMessage(ConversationId conversation, MessageRole role, string text, MessageState state, DateTimeOffset at) =>
        new(MessageId.New(), conversation, 0, role, ContentBlocks.FromText(text), state, 0, null, null, null, at, state == MessageState.Complete ? at : null);

    private static GenerationTaskRecord NewTask(ConversationId conversation, MessageId answer, DateTimeOffset at) =>
        new(GenerationTaskId.New(), GenerationTaskKind.Answer, conversation, answer, Model, [], at);

    /// <summary>Markdown with the features the renderer must handle: headings, lists, code, a table.</summary>
    private static string Answer(int characters)
    {
        var paragraph = """
            ## 蒸汽机与铁路

            瓦特改良的蒸汽机让**动力**摆脱了水力和畜力的限制。早期机车包括：

            - 特里维西克的“便捷号”（1804）
            - 斯蒂芬森的“火箭号”（1829）

            | 年份 | 事件 |
            | --- | --- |
            | 1825 | 斯托克顿—达灵顿铁路通车 |

            ```text
            efficiency = work / heat
            ```

            """;
        var builder = new StringBuilder(characters + paragraph.Length);
        while (builder.Length < characters)
        {
            builder.Append(paragraph);
        }

        return builder.ToString(0, characters);
    }
}
