using System.Runtime.CompilerServices;

namespace Mindweave.Core;

public interface IReplyStream
{
    IAsyncEnumerable<string> StreamAsync(string prompt, string? quote, CancellationToken cancellationToken);
}
public enum GenerationChange { Started, Appended, Finished }
public sealed record GenerationUpdate(Conversation Conversation, Message Message, GenerationChange Change);

public sealed class GenerationCoordinator(ExplorationState state, IReplyStream replies)
{
    private sealed class Running
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
    }
    private readonly Dictionary<string, Running> _running = [];
    public event Action<GenerationUpdate>? Updated;
    public bool HasRunning { get { lock (_running) return _running.Count > 0; } }
    public bool IsRunning(string conversationId) { lock (_running) return _running.ContainsKey(conversationId); }

    public Task GenerateAsync(string conversationId, string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return Task.CompletedTask;
        var conversation = state.Conversations.Single(x => x.Id == conversationId);
        var running = new Running();
        lock (_running)
        {
            if (_running.ContainsKey(conversationId)) throw new InvalidOperationException("这个会话仍在输出。");
            _running.Add(conversationId, running);
        }
        conversation.Messages.Add(new(Guid.NewGuid().ToString("N"), true, prompt.Trim()));
        var answer = new Message(Guid.NewGuid().ToString("N"), false, "", ReplyStatus.Generating);
        conversation.Messages.Add(answer);
        conversation.Draft = "";
        Updated?.Invoke(new(conversation, answer, GenerationChange.Started));
        running.Task = PumpAsync(conversation, answer, prompt.Trim(), running);
        return running.Task;
    }

    private async Task PumpAsync(Conversation conversation, Message answer, string prompt, Running running)
    {
        try
        {
            await foreach (var chunk in replies.StreamAsync(prompt, conversation.Source?.Quote, running.Cancellation.Token))
            {
                running.Cancellation.Token.ThrowIfCancellationRequested();
                answer.Markdown += chunk;
                Updated?.Invoke(new(conversation, answer, GenerationChange.Appended));
            }
            answer.Status = ReplyStatus.Complete;
        }
        catch (OperationCanceledException) when (running.Cancellation.IsCancellationRequested) { answer.Status = ReplyStatus.Cancelled; }
        catch { answer.Status = ReplyStatus.Failed; }
        finally
        {
            lock (_running) _running.Remove(conversation.Id);
            Updated?.Invoke(new(conversation, answer, GenerationChange.Finished));
            running.Cancellation.Dispose();
        }
    }

    public void Cancel(string conversationId)
    {
        lock (_running) if (_running.TryGetValue(conversationId, out var running)) running.Cancellation.Cancel();
    }

    public async Task CancelAllAsync()
    {
        Running[] running;
        lock (_running) running = _running.Values.ToArray();
        foreach (var item in running) item.Cancellation.Cancel();
        await Task.WhenAll(running.Select(x => x.Task));
    }
}

public sealed class FixedReplyStream(Func<string, string?, string> reply, TimeSpan? duration = null, int chunkSize = 9) : IReplyStream
{
    public async IAsyncEnumerable<string> StreamAsync(string prompt, string? quote, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var text = reply(prompt, quote);
        var chunks = Math.Max(1, (int)Math.Ceiling((double)text.Length / chunkSize));
        var interval = (duration ?? TimeSpan.FromSeconds(16)) / chunks;
        for (var start = 0; start < text.Length; start += chunkSize)
        {
            if (interval > TimeSpan.Zero) await Task.Delay(interval, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            yield return text.Substring(start, Math.Min(chunkSize, text.Length - start));
        }
    }
}
