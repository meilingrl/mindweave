using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Mindweave.Core;
using Xunit;

namespace Mindweave.Core.Tests;

public class StreamingTests
{
    [Fact]
    public async Task Generation_continues_in_its_original_conversation_after_switching()
    {
        var state = new ExplorationState([new("a", "A"), new("b", "B")]);
        var source = new ControlledReplyStream();
        var generation = new GenerationCoordinator(state, source);
        var updates = Channel.CreateUnbounded<GenerationUpdate>();
        generation.Updated += u => updates.Writer.TryWrite(u);
        var task = generation.GenerateAsync("a", "question-a");
        await updates.Reader.ReadAsync(); // Started
        state.SwitchTo("b", new(null, 14), "draft-a");
        await source.For("question-a").Writer.WriteAsync("**蒸");
        var append = await updates.Reader.ReadAsync();
        Assert.Equal("**蒸", append.Message.Markdown);
        Assert.Equal("a", append.Conversation.Id);
        Assert.Empty(state.Current.Messages);
        source.For("question-a").Writer.Complete();
        await task;
        Assert.Equal(ReplyStatus.Complete, state.Conversations[0].Messages.Last().Status);
    }

    [Fact]
    public async Task One_chat_rejects_a_second_answer_while_another_chat_can_generate()
    {
        var state = new ExplorationState([new("a", "A"), new("b", "B")]);
        var source = new ControlledReplyStream();
        var generation = new GenerationCoordinator(state, source);
        var first = generation.GenerateAsync("a", "one");
        Assert.Throws<InvalidOperationException>(() => { _ = generation.GenerateAsync("a", "duplicate"); });
        var second = generation.GenerateAsync("b", "two");
        Assert.True(generation.IsRunning("a"));
        Assert.True(generation.IsRunning("b"));
        Assert.Equal(2, state.Conversations[0].Messages.Count);
        await generation.CancelAllAsync();
        await Task.WhenAll(first, second);
        Assert.False(generation.HasRunning);
    }

    [Fact]
    public async Task Cancellation_preserves_received_text_and_records_partial_status()
    {
        var state = new ExplorationState([new("a", "A")]);
        var source = new ControlledReplyStream();
        var generation = new GenerationCoordinator(state, source);
        var appended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        generation.Updated += u => { if (u.Change == GenerationChange.Appended) appended.TrySetResult(); };
        var task = generation.GenerateAsync("a", "one");
        await source.For("one").Writer.WriteAsync("received partial text");
        await appended.Task.WaitAsync(TimeSpan.FromSeconds(5));
        generation.Cancel("a");
        await task;
        Assert.Equal("received partial text", state.Current.Messages.Last().Markdown);
        Assert.Equal(ReplyStatus.Cancelled, state.Current.Messages.Last().Status);
        Assert.False(generation.IsRunning("a"));
    }

    [Fact]
    public async Task Split_markdown_delimiters_are_preserved_exactly_in_the_completed_answer()
    {
        var state = new ExplorationState([new("a", "A")]);
        var source = new ControlledReplyStream();
        var generation = new GenerationCoordinator(state, source);
        var task = generation.GenerateAsync("a", "one");
        foreach (var chunk in new[] { "*", "*蒸汽", "机*", "*，[铁", "路](https://example.org)", "\n\n`", "``text\n代码", "\n`", "``" })
            await source.For("one").Writer.WriteAsync(chunk);
        source.For("one").Writer.Complete();
        await task;
        Assert.Equal("**蒸汽机**，[铁路](https://example.org)\n\n```text\n代码\n```", state.Current.Messages.Last().Markdown);
        var blocks = MarkdownDocument.Parse(state.Current.Messages.Last().Markdown);
        Assert.Equal("蒸汽机，铁路", blocks[0].Text);
        Assert.Equal(BlockKind.Code, blocks[1].Kind);
    }
}

public sealed class ControlledReplyStream : IReplyStream
{
    private readonly Dictionary<string, Channel<string>> _channels = [];
    public Channel<string> For(string prompt)
    {
        lock (_channels)
        {
            if (!_channels.TryGetValue(prompt, out var channel)) _channels[prompt] = channel = Channel.CreateUnbounded<string>();
            return channel;
        }
    }
    public async IAsyncEnumerable<string> StreamAsync(string prompt, string? quote, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var chunk in For(prompt).Reader.ReadAllAsync(cancellationToken)) yield return chunk;
    }
}
