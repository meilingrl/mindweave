using System.Runtime.CompilerServices;
using System.Text.Json;
using Mindweave.Application.Ai;
using Mindweave.Application.Conversations;
using Mindweave.Application.Models;
using Mindweave.Application.Tools;
using Mindweave.Core.Conversations;
using Mindweave.Core.Models;
using Mindweave.Core.Resources;
using Mindweave.Core.Runs;
using Mindweave.Infrastructure.Sqlite;
using Mindweave.Testing;

namespace Mindweave.Infrastructure.Tests.Chat;

/// <summary>
/// Reasoning shown live and one status per stage (docs/design/reasoning-and-status.md, requirements §10.4.4 v0.21): the
/// reasoning is in the running answer only, gone when it ends and never stored; the answer says which stage it is in.
/// </summary>
public class ReasoningAndStatusTests
{
    private static readonly ModelSettingsInput ToolModel = new(
        "fake", "https://fake.example/v1", "fake-model", "sk-test",
        Capabilities: ModelCapabilities.DefaultsFor(ChatProtocol.ChatCompletions) with { ToolCalls = true });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_reasoning_streams_into_the_running_answer_and_is_gone_and_unstored_once_it_ends()
    {
        await using var h = await ChatHarness.StartAsync();
        var held = h.Client.HoldThinking();
        var sent = await h.Conversations.StartAsync(h.Workspace, "火车", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var live = h.Conversations.Observe(sent.Answer!.Value)!;

        await ChatHarness.UntilAsync(() => live.Snapshot().Reasoning == string.Concat(FakeChatClient.ThinkingChunks));
        Assert.Equal(AnswerStage.Thinking, live.Snapshot().Stage);
        Assert.Equal(string.Empty, live.Snapshot().Text);

        held.Finish("蒸汽机车");
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal((MessageState.Complete, "蒸汽机车"), (answer.State, answer.Text));
        Assert.Equal(string.Empty, live.Snapshot().Reasoning);
        Assert.DoesNotContain(answer.Blocks, b => b is TextBlock t && t.Markdown.Contains(FakeChatClient.ThinkingChunks[0], StringComparison.Ordinal));
        Assert.Empty(await CellsContainingAsync(h, FakeChatClient.ThinkingChunks[0]));
    }

    [Fact]
    public async Task Reasoning_after_text_has_arrived_is_shown_while_the_answer_is_still_being_written()
    {
        var client = new TextThenReasoningClient();
        await using var h = await ChatHarness.StartAsync(client: client);

        var sent = await h.Conversations.StartAsync(h.Workspace, "火车", Ct);
        await client.Reasoned.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var live = h.Conversations.Observe(sent.Answer!.Value)!;

        Assert.Equal(("部分", "再想一下", AnswerStage.Writing), (live.Snapshot().Text, live.Snapshot().Reasoning, live.Snapshot().Stage));
        client.Release.TrySetResult();
        Assert.Equal(MessageState.Complete, (await h.WaitForAnswerAsync(sent.Answer!.Value)).State);
        Assert.Equal(string.Empty, live.Snapshot().Reasoning);
    }

    [Fact]
    public async Task A_stopped_answer_drops_its_reasoning_too()
    {
        await using var h = await ChatHarness.StartAsync();
        var held = h.Client.HoldThinking();
        var sent = await h.Conversations.StartAsync(h.Workspace, "火车", Ct);
        await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var live = h.Conversations.Observe(sent.Answer!.Value)!;
        await ChatHarness.UntilAsync(() => live.Snapshot().Reasoning.Length > 0);

        await h.Conversations.StopAsync(sent.Answer!.Value);
        await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(string.Empty, live.Snapshot().Reasoning);
        Assert.Empty(await CellsContainingAsync(h, FakeChatClient.ThinkingChunks[0]));
    }

    [Fact]
    public async Task Each_stage_of_a_run_is_said_in_turn_and_the_rounds_reasoning_is_kept_together()
    {
        var tool = new HeldTool();
        await using var h = await ChatHarness.StartAsync(model: ToolModel, tools: [tool]);
        h.Client.CallTools(string.Empty, [new ChatToolCall("c1", HeldTool.ToolName, "{\"q\":\"蒸汽机\"}")], reasoning: "先查一下。");
        h.Client.FailWithStatus(GenerationErrorKind.QuotaOrRateLimited, 429, TimeSpan.FromMilliseconds(30));
        h.Client.Reply("瓦特改良了蒸汽机。");

        var sent = await h.Conversations.StartAsync(h.Workspace, "蒸汽机是谁改良的？", Ct);
        await tool.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var live = h.Conversations.Observe(sent.Answer!.Value)!;
        var stages = new List<AnswerStage> { live.Snapshot().Stage };
        live.Changed += (_, _) =>
        {
            var stage = live.Snapshot().Stage;
            lock (stages)
            {
                if (stages[^1] != stage)
                {
                    stages.Add(stage);
                }
            }
        };

        Assert.Equal((AnswerStage.Tool, HeldTool.ToolName), (live.Snapshot().Stage, live.Snapshot().ActiveTool));
        Assert.Equal("先查一下。", live.Snapshot().Reasoning);
        tool.Open.TrySetResult();
        var answer = await h.WaitForAnswerAsync(sent.Answer!.Value);

        Assert.Equal(MessageState.Complete, answer.State);
        lock (stages)
        {
            // After the tool: a new round waits, its request fails by chance and is retried, the retried one writes.
            Assert.Equal([AnswerStage.Tool, AnswerStage.Waiting, AnswerStage.Retrying, AnswerStage.Writing], stages.TakeWhile(s => s != AnswerStage.Done));
        }
    }

    [Fact]
    public async Task The_last_request_after_a_limit_says_it_answers_from_what_was_found_until_its_text_arrives()
    {
        var tool = new HeldTool();
        await using var h = await ChatHarness.StartAsync(model: ToolModel, tools: [tool]);
        await h.ToolSettings.SetLimitsAsync(RunLimits.Default with { ModelRequests = 2 }, Ct);
        h.Client.CallTools("我先查一下。", [new ChatToolCall("c1", HeldTool.ToolName, "{\"q\":\"蒸汽机\"}")]);
        var final = h.Client.Hold();

        var sent = await h.Conversations.StartAsync(h.Workspace, "蒸汽机是谁改良的？", Ct);
        await tool.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var live = h.Conversations.Observe(sent.Answer!.Value)!;
        tool.Open.TrySetResult();
        await final.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        // Text of an earlier round does not make the new round "writing".
        await ChatHarness.UntilAsync(() => live.Snapshot().Stage == AnswerStage.Finishing);
        Assert.Equal("我先查一下。", live.Snapshot().Text);
        final.Push("瓦特。");
        await ChatHarness.UntilAsync(() => live.Snapshot().Stage == AnswerStage.Writing);
        final.Finish();
        Assert.Equal(MessageState.Complete, (await h.WaitForAnswerAsync(sent.Answer!.Value)).State);
        Assert.Equal(AnswerStage.Done, live.Snapshot().Stage);
    }

    [Fact]
    public async Task A_long_reasoning_keeps_only_its_latest_part()
    {
        var client = new LongReasoningClient(chunk: new string('想', 1000), chunks: 30);
        await using var h = await ChatHarness.StartAsync(client: client);

        var sent = await h.Conversations.StartAsync(h.Workspace, "火车", Ct);
        await client.Reasoned.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var reasoning = h.Conversations.Observe(sent.Answer!.Value)!.Snapshot().Reasoning;

        Assert.StartsWith("…", reasoning, StringComparison.Ordinal);
        Assert.Equal(LiveAnswer.MaxReasoning + 1, reasoning.Length);
        Assert.EndsWith("末尾", reasoning, StringComparison.Ordinal);
        client.Release.TrySetResult();
        Assert.Equal(MessageState.Complete, (await h.WaitForAnswerAsync(sent.Answer!.Value)).State);
    }

    /// <summary>Every text cell of every table that contains <paramref name="needle"/>, as "table.column".</summary>
    private static async Task<IReadOnlyList<string>> CellsContainingAsync(ChatHarness h, string needle) =>
        await h.Database.ReadAsync(
            c =>
            {
                var tables = new List<string>();
                using (var list = c.Command(null, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';"))
                using (var reader = list.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        tables.Add(reader.GetString(0));
                    }
                }

                var found = new List<string>();
                foreach (var table in tables)
                {
                    using var cmd = c.Command(null, $"SELECT * FROM \"{table}\";");
                    using var rows = cmd.ExecuteReader();
                    while (rows.Read())
                    {
                        for (var i = 0; i < rows.FieldCount; i++)
                        {
                            if (rows.GetValue(i) is string text && text.Contains(needle, StringComparison.Ordinal))
                            {
                                found.Add(table + "." + rows.GetName(i));
                            }
                        }
                    }
                }

                return (IReadOnlyList<string>)found;
            },
            Ct);

    /// <summary>A read-only tool that waits for <see cref="Open"/> once it is running.</summary>
    private sealed class HeldTool : ITool
    {
        public const string ToolName = "held_tool";

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Open { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => ToolName;

        public string DisplayName => "测试工具";

        public string Description => "测试用的工具";

        public string ParametersSchema => "{\"type\":\"object\"}";

        public ToolRisk Risk => ToolRisk.LocalRead;

        public TimeSpan Timeout => TimeSpan.FromSeconds(20);

        public Task<bool> IsAvailableAsync(ToolScope scope, CancellationToken cancellationToken) => Task.FromResult(true);

        public string Describe(JsonElement arguments) => arguments.TryGetProperty("q", out var q) ? q.GetString() ?? string.Empty : string.Empty;

        public async Task<ToolOutput> ExecuteAsync(ToolScope scope, JsonElement arguments, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Open.Task.WaitAsync(cancellationToken);
            return new ToolOutput("瓦特改良了蒸汽机。", "完成", ResourceProvenance.Workspace, []);
        }
    }

    /// <summary>Answers with some text, then reasons, then waits; titles get a plain reply.</summary>
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
            yield return new ChatStreamEvent.ReasoningDelta("再想一下");
            Reasoned.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            yield return new ChatStreamEvent.Completed(false);
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["fake-model"]);
    }

    /// <summary>Reasons <paramref name="chunks"/> times and then "末尾", waits for <see cref="Release"/>, then answers.</summary>
    private sealed class LongReasoningClient(string chunk, int chunks) : IChatClient
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

            for (var i = 0; i < chunks; i++)
            {
                yield return new ChatStreamEvent.ReasoningDelta(chunk);
            }

            yield return new ChatStreamEvent.ReasoningDelta("末尾");
            Reasoned.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            yield return new ChatStreamEvent.TextDelta("答");
            yield return new ChatStreamEvent.Completed(false);
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string apiKey, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>(["fake-model"]);
    }
}
